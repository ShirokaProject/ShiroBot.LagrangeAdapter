using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ShiroBot.LagrangeAdapter;

namespace ContractChecks;
internal static class TokenRelayChecks
{
    private const string Guid = "0123456789abcdef0123456789abcdef";
    private const string Qua = "V1_MAC_NQ_7.0.3_54395_GW_B";
    private const string Command = "trpc.o3.ecdh_access.EcdhAccess.SsoEstablishShareKey";
    private static readonly LagrangeTokenRelay.Identity Online = new(true, 123456789, Guid);
    private static void Check(bool value, string name) { if (!value) throw new Exception("Token relay: " + name); }
    internal static async Task Run()
    {
        Check(new LagrangeAdapterConfig().TokenRelayEnabled, "one default-on switch");
        Check(typeof(LagrangeAdapterConfig).GetProperty("TokenRelayPort") is null &&
              typeof(LagrangeAdapterConfig).GetProperty("TokenRelayKeyFile") is null, "no port or extra key configuration");
        var identity = Online; var sent = 0;
        var packet = JsonSerializer.Serialize(new { id = new string('a', 32), command = Command, body = "0102", uin = "123456789", guid = Guid });
        var handler = new Transport(": heartbeat\n\nevent: task\ndata: " + packet + "\n\nevent: task\ndata: " + packet + "\n\n");
        await using (var relay = Create(handler, () => identity, (command, body, _) => {
            Check(command == Command && body.SequenceEqual(new byte[] {1,2}), "whitelisted bytes reach SDK");
            Interlocked.Increment(ref sent); return Task.FromResult((0,new byte[]{3,4}));
        }))
        {
            await handler.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await handler.SecondGet.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(sent==1 && handler.Results==1, "duplicate SSE task executes exactly once");
        }
        foreach (var bad in new[] {
            new { id = new string('a',32), command = "arbitrary.command", body="0102", uin="123456789", guid=Guid },
            new { id = new string('a',32), command = Command, body="0102", uin="987654321", guid=Guid },
            new { id = new string('a',32), command = Command, body="0102", uin="123456789", guid=new string('b',32) },
            new { id = new string('a',32), command = Command, body="xx", uin="123456789", guid=Guid }
        })
        {
            var rejected = new Transport("event: task\ndata: "+JsonSerializer.Serialize(bad)+"\n\n");
            var calls=0;
            await using var relay=Create(rejected,()=>Online,(_,_,_)=> { calls++; return Task.FromResult((0,Array.Empty<byte>())); });
            await rejected.Reregistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(calls==0 && rejected.Results==0,"invalid command, identity or HEX never sends SSO");
        }
        var offline = new Transport("event: task\ndata: "+packet+"\n\n");
        identity=Online;
        await using (var relay=Create(offline,()=>identity,(_,_,_)=> {
            identity=identity with {Online=false}; return Task.FromResult((0,new byte[]{1}));
        }))
        {
            await offline.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            Check(offline.Results==0,"identity change prevents stale response submission");
        }
        var unsupported=new Transport(""){Reject=true};
        var stopped=Create(unsupported,()=>Online,(_,_,_)=>throw new Exception("must not send"));
        await unsupported.Registered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await stopped.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Console.WriteLine("Outbound relay reuses auth/backend path; SSE/reply, replay, identity, whitelist, fallback and cancellation checks passed (no QQ transport).");
    }
    private static LagrangeTokenRelay Create(HttpMessageHandler handler,Func<LagrangeTokenRelay.Identity> identity,
        Func<string,byte[],CancellationToken,Task<(int,byte[])>> send) => new(new LagrangeAdapterConfig {
            SignServer="https://sign.example.test/backends/mac/", SignToken="test-sign-credential"
        },Qua,identity,send,CancellationToken.None,handler);
    private sealed class Transport(string events) : HttpMessageHandler
    {
        public int Results, Registrations, Gets;
        public bool Reject;
        public TaskCompletionSource Registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reregistered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondGet = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            Check(request.Headers.Authorization?.ToString()=="Bearer test-sign-credential","existing auth reused");
            var path=request.RequestUri!.AbsolutePath;
            Check(path.StartsWith("/backends/mac/relay/"),"configured backend path retained");
            if (path.EndsWith("/register"))
            {
                var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation)).RootElement;
                Check(body.GetProperty("qua").GetString()==Qua && body.GetProperty("guid").GetString()==Guid &&
                    body.GetProperty("features")[0].GetProperty("value").GetString()==Guid,"actual identity registered");
                if (++Registrations>1) Reregistered.TrySetResult();
                Registered.TrySetResult();
                return new HttpResponseMessage(Reject?HttpStatusCode.NotFound:HttpStatusCode.OK) {
                    Content=new StringContent(JsonSerializer.Serialize(new {client=new string('c',64)}),Encoding.UTF8,"application/json") };
            }
            if (path.EndsWith("/events"))
            {
                Check(request.RequestUri.Query=="?client="+new string('c',64),"opaque lease scoped events");
                if (++Gets>1) { SecondGet.TrySetResult(); await Task.Delay(Timeout.Infinite,cancellation); }
                Delivered.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(events,Encoding.UTF8,"text/event-stream")};
            }
            Check(path.EndsWith("/result"),"fixed API routes only");
            var result=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation)).RootElement;
            Check(result.GetProperty("client").GetString()==new string('c',64) && result.GetProperty("uin").GetString()=="123456789" &&
                result.GetProperty("guid").GetString()==Guid && result.GetProperty("body").GetString()=="0304", "reply bound to original identity");
            Interlocked.Increment(ref Results); Submitted.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"code\":0}",Encoding.UTF8,"application/json")};
        }
    }
}
