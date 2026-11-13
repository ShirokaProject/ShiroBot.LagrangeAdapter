using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Lagrange.Core;

namespace ContractChecks;

internal static class SmsLoginChecks
{
    internal static async Task Run()
    {
        var assembly = typeof(BotContext).Assembly;
        var sessionType = assembly.GetType("Lagrange.Core.Internal.Logic.SmsLoginSession")!;
        var flow = assembly.GetType("Lagrange.Core.Internal.Logic.SmsLoginFlow")!.GetMethod("Run")!;
        var responseType = assembly.GetType("Lagrange.Core.Internal.Events.Login.LoginEventResp")!;
        var requestType = assembly.GetType("Lagrange.Core.Internal.Events.Login.LoginEventReq")!;
        object Reply(byte code, Dictionary<ushort, byte[]> tlvs) => Activator.CreateInstance(responseType, code, tlvs)!;
        static Dictionary<ushort, byte[]> Challenge() => new()
        {
            [0x104] = [1, 2, 3], [0x126] = [0, 0, 0, 4, 4, 5, 6, 7],
            [0x183] = [0, 0, 0, 0, 7, 91, 205, 21]
        };
        Dictionary<ushort, byte[]> Verified(bool wrongAccount = false) => new()
        {
            [0x104] = [8, 9], [0x113] = wrongAccount ? [0, 0, 1, 1] : [7, 91, 205, 21]
        };
        async Task Exercise(bool wrongAccount = false, bool rejectAccount = false, bool cancelAfterSms = false)
        {
            using var session = (IDisposable)Activator.CreateInstance(sessionType, 123456789L)!;
            using var cancellation = new CancellationTokenSource();
            var hash = (byte[])sessionType.GetProperty("PasswordHash")!.GetValue(session)!;
            var hashSnapshot = hash.ToArray();
            var commands = new List<int>();
            var asked = 0;
            async Task<object> Send(object input)
            {
                var command = Convert.ToInt32(requestType.GetProperty("Cmd")!.GetValue(input));
                commands.Add(command);
                if (!string.IsNullOrEmpty((string)requestType.GetProperty("Password")!.GetValue(input)!))
                    throw new Exception("Independent SMS flow supplied a QQ password.");
                if (requestType.GetProperty("Sms")!.GetValue(input) != session)
                    throw new Exception("Independent SMS steps changed transient session identity.");
                switch (command)
                {
                    case 0x11: return Reply(rejectAccount ? (byte)235 : (byte)208, Challenge());
                    case 0x13: return Reply(0, new() { [0x104] = [9, 8, 7] });
                    case 0x12:
                        if (asked != 1 || (string)requestType.GetProperty("Code")!.GetValue(input)! != "654321")
                            throw new Exception("SMS verification did not follow user submission.");
                        var salted = (byte[])sessionType.GetMethod("SaltedHash")!.Invoke(session, null)!;
                        var expected = new byte[24];
                        hashSnapshot.CopyTo(expected, 0);
                        BinaryPrimitives.WriteUInt64BigEndian(expected.AsSpan(16), 123456789);
                        if (!salted.SequenceEqual(MD5.HashData(expected))) throw new Exception("Temporary SMS proof has wrong salt encoding.");
                        return Reply(0, Verified(wrongAccount));
                    case 9:
                        if (!(bool)sessionType.GetProperty("Verified")!.GetValue(session)! ||
                            !((byte[])sessionType.GetProperty("Token")!.GetValue(session)!).SequenceEqual(new byte[] { 8, 9 }))
                            throw new Exception("Ticket exchange started before account proof verification.");
                        return Reply(0, new());
                    default: throw new Exception("Unexpected SMS command.");
                }
            }
            async Task<string> Ask(string? url, string phone)
            {
                asked++;
                if (commands.Count != 2 || commands[1] != 0x13) throw new Exception("SMS code requested before successful send.");
                if (cancelAfterSms) cancellation.Cancel();
                return "654321";
            }
            var factory = typeof(SmsLoginChecks).GetMethod(nameof(Sender), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(requestType, responseType);
            var send = factory.Invoke(null, [(Func<object, Task<object>>)Send]);
            try
            {
                await (Task)flow.Invoke(null, [session, send, (Func<string?, string, Task<string>>)Ask, cancellation.Token])!;
                if (wrongAccount || rejectAccount || cancelAfterSms) throw new Exception("Rejected SMS proof reached success.");
                if (!commands.SequenceEqual(new[] { 0x11, 0x13, 0x12, 9 }) || asked != 1)
                    throw new Exception("Independent SMS sequence is incorrect.");
            }
            catch (InvalidDataException) when (wrongAccount) { if (commands.Contains(9)) throw; }
            catch (InvalidOperationException) when (rejectAccount) { if (commands.Count != 1 || asked != 0) throw; }
            catch (OperationCanceledException) when (cancelAfterSms) { if (commands.Count != 2) throw; }
            session.Dispose();
            if (hash.Any(x => x != 0)) throw new Exception("Transient SMS key was retained after disposal.");
        }
        await Exercise();
        await Exercise(wrongAccount: true);
        await Exercise(rejectAccount: true);
        await Exercise(cancelAfterSms: true);
        using (var malformed = (IDisposable)Activator.CreateInstance(sessionType, 123456789L)!)
        {
            var challenge = Challenge(); challenge[0x126] = [0, 0, 255, 255];
            try { sessionType.GetMethod("AccountChecked")!.Invoke(malformed, [Reply(208, challenge)]); throw new Exception("Truncated nonce accepted."); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
        }
        Console.WriteLine("Independent SMS account/send/verify/ticket flow, temporary salted proof, account mismatch, cancellation and cleanup checks passed (scripted transport, no Tencent contact).");
    }

    private static Func<TRequest, Task<TResponse>> Sender<TRequest, TResponse>(Func<object, Task<object>> callback)
        where TRequest : class where TResponse : class => async request => (TResponse)await callback(request);
}
