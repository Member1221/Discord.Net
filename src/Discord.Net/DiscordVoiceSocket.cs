﻿using Discord.API.Models;
using Discord.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WebSocketMessage = Discord.API.Models.VoiceWebSocketCommands.WebSocketMessage;
using System.Text;
#if !DNXCORE50
using Opus.Net;
#endif

namespace Discord
{
	internal sealed partial class DiscordVoiceSocket : DiscordWebSocket
	{
		private ManualResetEventSlim _connectWaitOnLogin;
		private UdpClient _udp;
		private ConcurrentQueue<byte[]> _sendQueue;
		private string _myIp;
		private IPEndPoint _endpoint;
		private byte[] _secretKey;
		private string _mode;
		private bool _isFirst;
		private ushort _sequence;
		private uint _ssrc;
		private long _startTicks;
		private readonly Random _rand = new Random();

#if !DNXCORE50
		private OpusEncoder _encoder;
#endif

		public DiscordVoiceSocket(int timeout, int interval)
			: base(timeout, interval)
		{
			_connectWaitOnLogin = new ManualResetEventSlim(false);
			_sendQueue = new ConcurrentQueue<byte[]>();
#if !DNXCORE50
			_encoder = OpusEncoder.Create(24000, 1, Application.Voip);
#endif
		}

		protected override void OnConnect()
		{
			_udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
			_udp.AllowNatTraversal(true);
			_isFirst = true;
        }
		protected override void OnDisconnect()
		{
			_udp = null;
		}

		protected override Task[] CreateTasks(CancellationToken cancelToken)
		{
            return new Task[]
			{
				Task.Factory.StartNew(ReceiveAsync, cancelToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Result,
				Task.Factory.StartNew(SendAsync, cancelToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Result,
				Task.Factory.StartNew(WatcherAsync, cancelToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Result
			}.Concat(base.CreateTasks(cancelToken)).ToArray();
		}

		public async Task Login(string serverId, string userId, string sessionId, string token)
		{
			var cancelToken = _disconnectToken.Token;

			_connectWaitOnLogin.Reset();

			_sequence = 0;

			VoiceWebSocketCommands.Login msg = new VoiceWebSocketCommands.Login();
			msg.Payload.ServerId = serverId;
			msg.Payload.SessionId = sessionId;
			msg.Payload.Token = token;
			msg.Payload.UserId = userId;
			await SendMessage(msg, cancelToken);

			try
			{
				if (!_connectWaitOnLogin.Wait(_timeout, cancelToken)) //Waiting on JoinServer message
					throw new Exception("No reply from Discord server");
			}
			catch (OperationCanceledException)
			{
				throw new InvalidOperationException("Bad Token");
			}

			SetConnected();
		}

		private async Task ReceiveAsync()
		{
			var cancelToken = _disconnectToken.Token;

			try
			{
				while (!cancelToken.IsCancellationRequested)
				{
					var result = await _udp.ReceiveAsync();					
					ProcessUdpMessage(result);
				}
			}
			catch { }
			finally { _disconnectToken.Cancel(); }
		}
		private async Task SendAsync()
		{
			var cancelToken = _disconnectToken.Token;
			try
			{
				byte[] bytes;
				while (!cancelToken.IsCancellationRequested)
				{
					while (_sendQueue.TryDequeue(out bytes))
						await _udp.SendAsync(bytes, bytes.Length);
					await Task.Delay(_sendInterval);
				}
			}
			catch { }
			finally { _disconnectToken.Cancel(); }
		}
		private async Task WatcherAsync()
		{
			try
			{
				await Task.Delay(-1, _disconnectToken.Token);
			}
			catch (TaskCanceledException) { }
#if DNXCORE50
			finally { _udp.Dispose(); }
#else
			finally { _udp.Close(); }
#endif
        }

		protected override async Task ProcessMessage(string json)
		{
			var msg = JsonConvert.DeserializeObject<WebSocketMessage>(json);
			switch (msg.Operation)
			{
				case 2: //READY
					{
						var payload = (msg.Payload as JToken).ToObject<VoiceWebSocketEvents.Ready>();
						_heartbeatInterval = payload.HeartbeatInterval;
						_endpoint = new IPEndPoint((await Dns.GetHostAddressesAsync(_host)).FirstOrDefault(), payload.Port);
						//_mode = payload.Modes.LastOrDefault();
						_mode = "plain";
                        _udp.Connect(_endpoint);
						lock(_rand)
						{
							_sequence = (ushort)_rand.Next(0, ushort.MaxValue);
							_startTicks = DateTime.UtcNow.Ticks - _rand.Next();
						}
						_ssrc = payload.SSRC;

						_sendQueue.Enqueue(new byte[70] {
							(byte)((_ssrc >> 24) & 0xFF),
							(byte)((_ssrc >> 16) & 0xFF),
							(byte)((_ssrc >> 8) & 0xFF),
							(byte)((_ssrc >> 0) & 0xFF),
							0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
							0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
							0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
							0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
							0x0, 0x0, 0x0, 0x0, 0x0, 0x0
						});
					}
					break;
				case 4: //SESSION_DESCRIPTION
					{
						var payload = (msg.Payload as JToken).ToObject<VoiceWebSocketEvents.JoinServer>();
						_secretKey = payload.SecretKey;
                        _connectWaitOnLogin.Set();
					}
					break;
				default:
					RaiseOnDebugMessage("Unknown WebSocket operation ID: " + msg.Operation);
					break;
			}
		}
		private void ProcessUdpMessage(UdpReceiveResult msg)
		{
			if (msg.Buffer.Length > 0 && msg.RemoteEndPoint.Equals(_endpoint))
			{
				byte[] buffer = msg.Buffer;
				int length = msg.Buffer.Length;
				if (_isFirst)
				{
					_isFirst = false;
					if (length != 70)
						throw new Exception($"Unexpected message length. Expected 70, got {length}.");

					int port = buffer[68] | buffer[69] << 8;

					_myIp = Encoding.ASCII.GetString(buffer, 4, 70 - 6).TrimEnd('\0');

					var login2 = new VoiceWebSocketCommands.Login2();
					login2.Payload.Protocol = "udp";
					login2.Payload.SocketData.Address = _myIp;
					login2.Payload.SocketData.Mode = _mode;
					login2.Payload.SocketData.Port = port;
					QueueMessage(login2);
				}
				else
				{
					//Parse RTP Data
					/*if (length < 12)
						throw new Exception($"Unexpected message length. Expected >= 12, got {length}.");

					byte flags = buffer[0];
					if (flags != 0x80)
						throw new Exception("Unexpected Flags");

					byte payloadType = buffer[1];
					if (payloadType != 0x78)
						throw new Exception("Unexpected Payload Type");

					ushort sequenceNumber = (ushort)((buffer[2] << 8) | buffer[3]);
					uint timestamp = (uint)((buffer[4] << 24) | (buffer[5] << 16) |
											(buffer[6] << 8) | (buffer[7] << 0));
					uint ssrc = (uint)((buffer[8] << 24) | (buffer[9] << 16) |
											 (buffer[10] << 8) | (buffer[11] << 0));

					//Decrypt
					if (_mode == "xsalsa20_poly1305")
					{
						if (length < 36) //12 + 24
							throw new Exception($"Unexpected message length. Expected >= 36, got {length}.");

#if !DNXCORE50
						byte[] nonce = new byte[24]; //16 bytes static, 8 bytes incrementing?
						Buffer.BlockCopy(buffer, 12, nonce, 0, 24);

						byte[] cipherText = new byte[buffer.Length - 36];
						Buffer.BlockCopy(buffer, 36, cipherText, 0, cipherText.Length);
						
						Sodium.SecretBox.Open(cipherText, nonce, _secretKey);
#endif
					}
					else //Plain
					{
						byte[] newBuffer = new byte[buffer.Length - 12];
						Buffer.BlockCopy(buffer, 12, newBuffer, 0, newBuffer.Length);
						buffer = newBuffer;
					}*/

					//TODO: Use Voice Data
				}
			}
        }

#if !DNXCORE50
		public void SendWAV(byte[] buffer, int count)
		{
			int encodedLength;
			buffer = _encoder.Encode(buffer, count, out encodedLength);
			byte[] packet = new byte[12 + encodedLength];
			Buffer.BlockCopy(buffer, 0, packet, 12, encodedLength);

			ushort sequence = _sequence++;
			long timestamp = (DateTime.UtcNow.Ticks - _startTicks) >> 2; //200ns resolution
            packet[0] = 0x80; //Flags;
			packet[1] = 0x78; //Payload Type
			packet[2] = (byte)((sequence >> 8) & 0xFF);
			packet[3] = (byte)((sequence >> 0) & 0xFF);
			packet[4] = (byte)((timestamp >> 24) & 0xFF);
			packet[5] = (byte)((timestamp >> 16) & 0xFF);
			packet[6] = (byte)((timestamp >> 8) & 0xFF);
			packet[7] = (byte)((timestamp >> 0) & 0xFF);
			packet[8] = (byte)((_ssrc >> 24) & 0xFF);
			packet[9] = (byte)((_ssrc >> 16) & 0xFF);
			packet[10] = (byte)((_ssrc >> 8) & 0xFF);
			packet[11] = (byte)((_ssrc >> 0) & 0xFF);
		}
#endif

		protected override object GetKeepAlive()
		{
			return new VoiceWebSocketCommands.KeepAlive();
		}
	}
}