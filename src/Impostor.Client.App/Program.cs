using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Impostor.Api.Games;
using Impostor.Api.Innersloth;
using Impostor.Api.Innersloth.GameOptions;
using Impostor.Api.Net.Messages;
using Impostor.Api.Net.Messages.C2S;
using Impostor.Hazel;
using Impostor.Hazel.Abstractions;
using Impostor.Hazel.Udp;
using Serilog;

namespace Impostor.Client.App
{
    internal static class Program
    {
        private static readonly ManualResetEvent QuitEvent = new ManualResetEvent(false);

        private static async Task Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .CreateLogger();

            var writeHandshake = MessageWriter.Get(MessageType.Reliable);

            // Handshake with current game version (2025.10.14)
            var gameVersion = new GameVersion(2025, 10, 14, 0);
            writeHandshake.Write(gameVersion);
            writeHandshake.Write("AeonLucid");
            writeHandshake.Write((uint)0); // lastNonceReceived (always 0 since 2021.11.9)
            writeHandshake.Write((uint)Language.English); // language
            writeHandshake.Write((byte)QuickChatModes.FreeChatOrQuickChat); // chatMode

            // Platform-specific data (empty message)
            writeHandshake.StartMessage(0);
            writeHandshake.EndMessage();
            writeHandshake.Write((int)CrossplayFlags.All); // crossplayFlags
            writeHandshake.Write((byte)0); // unknown purpose field

            var writeGameCreate = MessageWriter.Get(MessageType.Reliable);

            Message00HostGameC2S.Serialize(writeGameCreate, new NormalGameOptions
            {
                MaxPlayers = 4,
                NumImpostors = 2,
            }, CrossplayFlags.All, GameFilterOptions.CreateDefault());

            // TODO: ObjectPool for MessageReaders
            using (var connection = new UdpClientConnection(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 22023), null))
            {
                var e = new ManualResetEvent(false);

                // Register events.
                connection.DataReceived = DataReceived;
                connection.Disconnected = Disconnected;

                // Connect and send handshake.
                await connection.ConnectAsync(writeHandshake.ToByteArray(false));
                Log.Information("Connected.");

                // Create a game.
                await connection.SendAsync(writeGameCreate);
                Log.Information("Requested game creation.");

                // Recycle.
                writeHandshake.Recycle();
                writeGameCreate.Recycle();

                e.WaitOne();
            }
        }

        private static ValueTask DataReceived(DataReceivedEventArgs e)
        {
            using var reader = e.Message.ReadMessage();

            if (reader.Tag == byte.MaxValue)
            {
                // Acknowledgement packet
                Log.Information("Received acknowledgement packet");
                return default;
            }

            Log.Information("Received message with tag: {Tag} ({TagName})", reader.Tag, MessageFlags.FlagToString(reader.Tag));

            try
            {
                switch (reader.Tag)
                {
                    case MessageFlags.JoinedGame:
                        HandleJoinedGame(reader);
                        break;
                    case MessageFlags.Redirect:
                        HandleRedirect(reader);
                        break;
                    case MessageFlags.WaitForHost:
                        Log.Information("Server is waiting for host");
                        break;
                    default:
                        Log.Warning("Unhandled message type: {Tag}", reader.Tag);
                        break;
                }
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "Error processing message");
            }

            return default;
        }

        private static void HandleJoinedGame(IMessageReader reader)
        {
            var gameCode = reader.ReadInt32();
            var clientId = reader.ReadInt32();
            var hostId = reader.ReadInt32();

            Log.Information("Successfully joined game! Code: {GameCode}, ClientId: {ClientId}, HostId: {HostId}",
                GameCode.From(gameCode), clientId, hostId);

            // Success! Set the quit event to exit
            QuitEvent.Set();
        }

        private static void HandleRedirect(IMessageReader reader)
        {
            var ip = reader.ReadBytesAndSize();
            var port = reader.ReadUInt16();

            Log.Information("Server requested redirect to {Ip}:{Port}",
                string.Join(".", ip), port);
        }

        private static ValueTask Disconnected(DisconnectedEventArgs e)
        {
            Log.Information("Disconnected: " + e.Reason);
            QuitEvent.Set();
            return default;
        }
    }
}
