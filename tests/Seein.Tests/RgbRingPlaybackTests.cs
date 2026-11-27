using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Seein.Core;
using Seein.Core.Lighting;
using Seein.Service;
using static Seein.Core.Lighting.OpenRgbProtocol;

namespace Seein.Tests;

public sealed class RgbRingPlaybackTests
{
    [Fact]
    public async Task RingPlaybackBypassesSdkFramesAndRecoversAfterAWriteFailure()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12));var token=deadline.Token;
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var sdkWrites=new ConcurrentQueue<int>();var server=Serve(listener,sdkWrites,token);
        await using var provider=new OpenRgbProvider(((IPEndPoint)listener.LocalEndpoint).Port,5);
        var board=new Board();
        using var service=new RgbService(NullLogger<RgbService>.Instance,new PanelState(),new RgbEngineHost(NullLogger<RgbEngineHost>.Instance),provider,board);
        var wave=new RgbEffectSettings(RgbEffects.Wave,[new(40,146,255),new(38,219,255),new(54,245,180),new(187,114,255)],Speed:34);
        try
        {
            await provider.RefreshAsync(token:token);
            service.Initialize(RgbEffects.DefaultSettings() with{Enabled=true,GroupEffect=wave,AutoStartEngine=false,Fps=25});
            await service.StartAsync(token);await Until(()=>board.Frames.Count>=8,token);
            Assert.Single(board.Applications);Assert.Equal(3,board.Applications.Single());
            Assert.Contains("冷排连续流光",service.GetStatus().GroupPlayback);
            Assert.All(board.Frames,f=>Assert.Equal(68,f.Count));Assert.True(board.Verifications>0);Assert.Empty(sdkWrites);

            var native=wave with{EffectId=RgbEffects.Static};
            Assert.True((await service.HandleCommandAsync(new("groupEffect",GroupEffect:native),token)).Success);
            Assert.Null(board.Applications.Last());var frameCount=board.Frames.Count;
            await Task.Delay(180,token);Assert.Equal(frameCount,board.Frames.Count);
            Assert.True((await service.HandleCommandAsync(new("groupEffect",GroupEffect:wave),token)).Success);
            await Until(()=>board.Frames.Count>frameCount,token);Assert.Equal(3,board.Applications.Last());

            board.FailFrames=true;await Until(()=>service.GetStatus().LastError is not null,token);
            var applications=board.Applications.Count;await Task.Delay(300,token);
            Assert.Equal(applications,board.Applications.Count);Assert.Null(service.GetStatus().GroupPlayback);Assert.Empty(sdkWrites);
            board.FailFrames=false;
            // Recover through the normal two-second retry, without a manual reapply.
            await Until(()=>service.GetStatus().LastError is null&&service.GetStatus().GroupPlayback is not null,token);
            Assert.True(board.Applications.Count>applications);Assert.Empty(sdkWrites);
            Assert.Equal(wave.Colors,service.Settings.GroupEffect!.Colors);
            Assert.Equal(wave with{Colors=service.Settings.GroupEffect.Colors},service.Settings.GroupEffect);
        }
        finally{await service.StopAsync(CancellationToken.None);await provider.DisposeAsync();await server;}
    }

    private static async Task Until(Func<bool> done,CancellationToken token)
    {while(!done())await Task.Delay(15,token);}

    private sealed class Board:IMsiMysticLightBoard
    {
        public ConcurrentQueue<int?> Applications{get;}=new();
        public ConcurrentQueue<IReadOnlyList<RgbColor>> Frames{get;}=new();
        public bool FailFrames;public int Verifications;
        public bool Matches(RgbDeviceInfo info)=>info.Name=="MSI MYSTIC LIGHT";
        public RgbWriteResult Apply(RgbDeviceInfo info,RgbEffectSettings effect,IReadOnlyList<RgbColor>? jargb2Colors=null,int? ringZone=null)
        {Applications.Enqueue(ringZone);return RgbWriteResult.ReadBack;}
        public RgbWriteResult UpdateRing(RgbDeviceInfo info,int zone,IReadOnlyList<RgbColor> frame,bool verify)
        {Assert.Equal(3,zone);if(FailFrames)return RgbWriteResult.Reject("Simulated HID failure");Frames.Enqueue(frame);if(verify)Interlocked.Increment(ref Verifications);return RgbWriteResult.Sent;}
        public RgbWriteResult EnterDirect(RgbDeviceInfo info)=>throw new InvalidOperationException("SDK Direct must not be selected");
        public RgbWriteResult UpdateAudio(byte master,byte left,byte right)=>RgbWriteResult.Sent;
    }

    private static async Task Serve(TcpListener listener,ConcurrentQueue<int> writes,CancellationToken token)
    {
        using var client=await listener.AcceptTcpClientAsync(token);using var stream=client.GetStream();var header=new byte[16];var payload=Controller();
        while(await stream.ReadAsync(header.AsMemory(0,1),token)!=0)
        {
            await stream.ReadExactlyAsync(header.AsMemory(1),token);var(id,packet,length)=ParseHeader(header);var body=new byte[length];await stream.ReadExactlyAsync(body,token);
            byte[]? response=packet switch{RequestProtocol=>Int32Payload(5),ControllerCount=>Int32Payload(1),ControllerData=>payload,_=>null};
            if(response is not null){await stream.WriteAsync(BuildHeader(id,packet,response.Length),token);await stream.WriteAsync(response,token);}
            else if(packet!=SetClientName)writes.Enqueue(packet);
        }
    }
    private static byte[] Controller()
    {
        using var memory=new MemoryStream();using var w=new BinaryWriter(memory,Encoding.UTF8,true);
        void Text(string s){var bytes=Encoding.UTF8.GetBytes(s+'\0');w.Write((ushort)bytes.Length);w.Write(bytes);}
        w.Write(0);w.Write(0);Text("MSI MYSTIC LIGHT");Text("MSI");Text("Simulated");Text("test");Text("test-board");Text("test-location");
        w.Write((ushort)1);w.Write(0);Text("Direct");w.Write(100);
        foreach(var value in new uint[]{32,0,0,0,100,0,0,0,100,0,0})w.Write(value);w.Write((ushort)0);
        w.Write((ushort)4);var sizes=new[]{0,152,32,68};var names=new[]{"JAF","JARGB 1","JARGB 2","JARGB 3"};
        for(int i=0;i<4;i++){Text(names[i]);w.Write(1);w.Write(0);w.Write(240);w.Write(sizes[i]);w.Write((ushort)0);w.Write((ushort)0);w.Write(3u);}
        w.Write((ushort)252);for(int i=0;i<252;i++){Text("LED "+i);w.Write(0u);}
        w.Write((ushort)252);for(int i=0;i<252;i++)w.Write(0u);w.Write((ushort)0);w.Write(0u);w.Flush();
        var bytes=memory.ToArray();BinaryPrimitives.WriteInt32LittleEndian(bytes,bytes.Length);return bytes;
    }
}
