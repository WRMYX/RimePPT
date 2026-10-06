using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RimePPT.Core;
using RimePPT.Services;

internal static class GitHubUpdateTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    public static void Register(Action<string, Action> test, Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "RimePPT-update-tests-" + Guid.NewGuid().ToString("N"));
        test("GitHub download verifies exact size and SHA256", () => {
            byte[] bytes = Encoding.UTF8.GetBytes("release-content");
            using var client = new HttpClient(new Handler((_,_) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
            var service = new GitHubUpdateService(client, root);
            var asset = new GitHubAsset("test.zip",new Uri("https://github.com/WRMYX/RimePPT/releases/download/v1.2.3/test.zip"),bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)));
            var progress = new Progress<double>();
            string file = service.DownloadAsync(asset,progress,CancellationToken.None).GetAwaiter().GetResult();
            check(File.ReadAllBytes(file).SequenceEqual(bytes),"exact downloaded bytes");
            foreach(var bad in new[]{asset with { Sha256=new string('0',64) },asset with { Size=bytes.Length+1 },asset with { Size=bytes.Length-1 }}) {
                bool rejected=false;try{service.DownloadAsync(bad,progress,CancellationToken.None).GetAwaiter().GetResult();}catch(InvalidDataException){rejected=true;}
                check(rejected,"bad download rejected");
            }
            check(Directory.GetFiles(root,"download.zip",SearchOption.AllDirectories).Length==1,"partial and failed files removed");
        });
        test("accelerated download uses selected URL and official hash", () => {
            byte[] bytes = Encoding.UTF8.GetBytes("proxy-content");
            string? requested = null;
            using var client = new HttpClient(new Handler((request, _) => {
                requested = request.RequestUri!.AbsoluteUri;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }));
            var asset = new GitHubAsset("test.zip", new Uri("https://github.com/WRMYX/RimePPT/releases/download/v1.2.4/test.zip"), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
            var service = new GitHubUpdateService(client, root);
            service.DownloadAsync(asset, new Progress<double>(), CancellationToken.None, "ghproxy").GetAwaiter().GetResult();
            check(requested == "https://gh-proxy.org/" + asset.Url.AbsoluteUri, "selected proxy requested");
            bool rejected = false;
            try { service.DownloadAsync(asset with { Sha256 = new string('0', 64) }, new Progress<double>(), CancellationToken.None, "custom", "https://example.com/").GetAwaiter().GetResult(); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "proxy bytes still require official hash");
        });
        test("GitHub check handles inaccessible release and request limits", () => {
            foreach(var status in new[]{HttpStatusCode.NotFound,HttpStatusCode.Forbidden,HttpStatusCode.TooManyRequests}) {
                using var client=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(status))));
                bool rejected=false;try{new GitHubUpdateService(client,root).CheckAsync(CancellationToken.None).GetAwaiter().GetResult();}catch(InvalidOperationException){rejected=true;}
                check(rejected,"HTTP status "+status);
            }
        });
        test("GitHub requests can be cancelled", () => {
            using var client=new HttpClient(new Handler(async (_,token)=>{await Task.Delay(Timeout.Infinite,token);return new HttpResponseMessage(HttpStatusCode.OK);}));
            using var cancellation=new CancellationTokenSource(50);
            bool cancelled=false;try{new GitHubUpdateService(client,root).CheckAsync(cancellation.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
            check(cancelled,"HTTP cancellation propagated");
        });
        test("GitHub extraction rejects traversal and missing runtime", () => {
            Directory.CreateDirectory(root);
            foreach(string entry in new[]{"../escape.txt","RimePPT/only.txt"}) {
                string zip=Path.Combine(root,Guid.NewGuid()+".zip");
                using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)) using(var writer=new StreamWriter(archive.CreateEntry(entry).Open()))writer.Write("data");
                // Each extraction gets its own staging directory.
                string folder=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
                string moved=Path.Combine(folder,"download.zip");File.Move(zip,moved);
                bool rejected=false;try{GitHubUpdateService.Extract(moved,GitHubUpdateChannel.Portable);}catch(InvalidDataException){rejected=true;}
                check(rejected,"unsafe or incomplete archive rejected");
            }
            check(!File.Exists(Path.Combine(root,"escape.txt")),"no traversal write");
        });
    }
}
