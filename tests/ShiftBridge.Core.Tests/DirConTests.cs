using System.Buffers.Binary;
using System.Net.Sockets;
using ShiftBridge.Core.DirCon;
using ShiftBridge.Core.Obc;
using ShiftBridge.Platform.Obc;

namespace ShiftBridge.Core.Tests;

public class DirConPacketTests
{
    [Fact]
    public void Uuid_is_big_endian_on_the_wire()
    {
        var b = DirConPacket.UuidBytes(new Guid("00001818-0000-1000-8000-00805f9b34fb"));
        Assert.Equal(Convert.FromHexString("00001818000010008000" + "00805F9B34FB"), b);
        Assert.Equal(new Guid("00001818-0000-1000-8000-00805f9b34fb"), DirConPacket.ReadUuid(b));
    }

    [Fact]
    public void Header_layout_and_length_are_big_endian()
    {
        var bytes = new DirConPacket(0x06, 0, 0, new byte[300]).Encode();
        Assert.Equal(new byte[] { 0x01, 0x06, 0x00, 0x00, 0x01, 0x2C }, bytes[..6]); // 300 = 0x012C
    }

    [Fact]
    public void Stream_parser_handles_split_and_concatenated_packets()
    {
        var a = new DirConPacket(0x01, 1, 0, []).Encode();
        var b = new DirConPacket(0x03, 2, 0, new byte[16]).Encode();
        var all = a.Concat(b).ToArray();
        var parser = new DirConStreamParser();
        var packets = new List<DirConPacket>();
        foreach (var x in all) packets.AddRange(parser.Feed([x]));
        Assert.Equal(2, packets.Count);
        Assert.Equal(2, packets[1].Sequence);
    }
}

public class DirConServerTests
{
    private static async Task<DirConPacket> RequestAsync(NetworkStream s, DirConStreamParser parser, DirConPacket req, CancellationToken ct)
    {
        await s.WriteAsync(req.Encode(), ct);
        // Read only the reply. The first notification may follow it in the same TCP segment.
        var header = new byte[DirConPacket.HeaderSize];
        await s.ReadExactlyAsync(header, ct);
        var frame = new byte[header.Length + BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4))];
        header.CopyTo(frame, 0);
        await s.ReadExactlyAsync(frame.AsMemory(header.Length), ct);
        return Assert.Single(parser.Feed(frame));
    }

    [Fact]
    public async Task Full_dircon_session_discovery_subscribe_appinfo_and_notification()
    {
        await using var server = new DirConServer();
        var appInfo = new TaskCompletionSource<ObcAppInfo>();
        server.AppInfoReceived += appInfo.SetResult;
        await server.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var s = client.GetStream();
        var parser = new DirConStreamParser();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // 1) services
        var r1 = await RequestAsync(s, parser, new DirConPacket(0x01, 1, 0, []), cts.Token);
        Assert.Equal(1, r1.Sequence);
        Assert.Equal(ObcGattUuids.Service, DirConPacket.ReadUuid(r1.Payload));

        // 2) caractéristiques : 16 + 3 x 17 octets
        var svc = DirConPacket.UuidBytes(ObcGattUuids.Service);
        var r2 = await RequestAsync(s, parser, new DirConPacket(0x02, 2, 0, svc), cts.Token);
        Assert.Equal(16 + 3 * 17, r2.Payload.Length);
        Assert.Equal(0x05, r2.Payload[16 + 16]); // Button State : Read | Notify

        // 3) activation des notifications
        byte[] enable = [.. DirConPacket.UuidBytes(ObcGattUuids.ButtonState), 0x01];
        var r3 = await RequestAsync(s, parser, new DirConPacket(0x05, 3, 0, enable), cts.Token);
        Assert.Equal(DirConResponse.Success, r3.ResponseCode);

        // 4) écriture App Info
        var info = ObcMessages.EncodeAppInfo(new ObcAppInfo("trainingpeaks", "3.0", []));
        byte[] write = [.. DirConPacket.UuidBytes(ObcGattUuids.AppInfo), .. info];
        var r4 = await RequestAsync(s, parser, new DirConPacket(0x04, 4, 0, write), cts.Token);
        Assert.Equal(DirConResponse.Success, r4.ResponseCode);
        Assert.Equal("trainingpeaks", (await appInfo.Task.WaitAsync(cts.Token)).AppId);

        // 5) notification initiale vide envoyée après App Info (TPV l'attend pour finir sa connexion),
        //    puis notification de bouton (séquence 0). Les deux paquets peuvent arriver dans la même lecture TCP.
        await server.SendButtonStateAsync([0x01, 0x01, 0x01]);
        var notifs = new List<DirConPacket>();
        var buf = new byte[256];
        while (notifs.Count < 2)
        {
            int n = await s.ReadAsync(buf, cts.Token);
            notifs.AddRange(parser.Feed(buf.AsSpan(0, n)));
        }
        Assert.All(notifs, p => { Assert.Equal((byte)DirConId.Notification, p.Id); Assert.Equal(0, p.Sequence); });
        Assert.Equal(new byte[] { 0x01 }, notifs[0].Payload[16..]);                 // état vide initial
        Assert.Equal(new byte[] { 0x01, 0x01, 0x01 }, notifs[1].Payload[16..]);     // Shift Up appuyé
    }

    [Fact]
    public async Task TrainingPeaks_style_enable_request_without_flag_byte_subscribes()
    {
        // Séquence RÉELLE de TPV 0.4.41 : l'activation ne contient que l'UUID (16 octets).
        await using var server = new DirConServer();
        await server.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var s = client.GetStream();
        var parser = new DirConStreamParser();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var enable = Convert.FromHexString("010500000010D273F681D548419DB9D1FA0472345229");
        await s.WriteAsync(enable, cts.Token);
        var buf = new byte[256];
        int n = await s.ReadAsync(buf, cts.Token);
        // Réponse attendue : en-tête (code 0, longueur 16) + UUID = 22 octets, jamais l'erreur de 6 octets.
        Assert.Equal("010500000010D273F681D548419DB9D1FA0472345229", Convert.ToHexString(buf, 0, n));

        await server.SendButtonStateAsync([0x01, 0x01, 0x01]);
        DirConPacket? notif = null;
        while (notif is null)
        {
            n = await s.ReadAsync(buf, cts.Token);
            notif = parser.Feed(buf.AsSpan(0, n)).FirstOrDefault();
        }
        Assert.Equal((byte)DirConId.Notification, notif.Id);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x01 }, notif.Payload[16..]);
    }

    [Fact]
    public async Task Unknown_service_and_characteristic_return_error_codes()
    {
        await using var server = new DirConServer();
        await server.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var s = client.GetStream();
        var parser = new DirConStreamParser();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var bad = DirConPacket.UuidBytes(Guid.NewGuid());
        var r1 = await RequestAsync(s, parser, new DirConPacket(0x02, 1, 0, bad), cts.Token);
        Assert.Equal(DirConResponse.ServiceNotFound, r1.ResponseCode);
        var r2 = await RequestAsync(s, parser, new DirConPacket(0x03, 2, 0, bad), cts.Token);
        Assert.Equal(DirConResponse.CharacteristicNotFound, r2.ResponseCode);
    }
}
