using System.Security.Cryptography;
using Lunalyr.Install;

// Lunalyr's packaging tool. Commands (see docs/PACKAGING.md):
//   make     --app <published app folder> --setup <setup exe for inside the payload> --stub <unsigned setup exe>
//            --version <x.y.z> --out <installer.exe>
//   feed     --installer <installer.exe> --version <x.y.z> --url <https download address> [--notes <text>] --out <update.json>
//   sign     --feed <update.json> --key <private key .pem>            (writes update.json.sig)
//   verify   --feed <update.json> --public <Base64 public key>        (checks the .sig beside it)
//   keygen   --private <new .pem file, kept secret, outside the repo> (prints the public key for the build)
// Nothing here uploads or publishes anything.

var command = args.Length > 0 ? args[0] : "";
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 1; i + 1 < args.Length; i += 2) options[args[i]] = args[i + 1];
string Need(string name) => options.TryGetValue(name, out var v) ? v : throw new ArgumentException($"{command} needs {name}.");

try
{
    switch (command)
    {
        case "make":
        {
            var version = Need("--version");
            if (ProductVersion.Parse(version) is null) throw new ArgumentException($"{version} isn't a version.");
            var output = Path.GetFullPath(Need("--out"));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var zipPath = output + ".zip.tmp";
            using (var zip = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.DeleteOnClose))
            {
                Payload.WriteZip(zip, Need("--app"), Need("--setup"), version);
                Payload.WriteInstaller(Need("--stub"), zip, output);
            }
            using var check = File.OpenRead(output);
            var payload = Payload.Locate(check) ?? throw new InvalidDataException("The new installer has no payload.");
            if (!Payload.Verify(check, payload)) throw new InvalidDataException("The new installer's payload doesn't verify.");
            Console.WriteLine($"{output}: {new FileInfo(output).Length:N0} bytes, payload {payload.Length:N0} bytes, Lunalyr {version}.");
            break;
        }
        case "feed":
        {
            var installer = Need("--installer");
            var version = Need("--version");
            if (ProductVersion.Parse(version) is null) throw new ArgumentException($"{version} isn't a version.");
            var url = Need("--url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !UpdateConfiguration.IsSafeDownload(uri))
                throw new ArgumentException("--url must be an https address with no credentials.");
            using var stream = File.OpenRead(installer);
            var feed = new UpdateFeed(Product.Name, version, url, Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length,
                options.GetValueOrDefault("--notes"), DateTimeOffset.UtcNow);
            File.WriteAllBytes(Need("--out"), UpdateFeeds.Serialize(feed));
            Console.WriteLine($"Wrote {Need("--out")} (unsigned: run sign next).");
            break;
        }
        case "sign":
        {
            var feed = Need("--feed");
            var signature = UpdateFeeds.Sign(File.ReadAllBytes(feed), File.ReadAllText(Need("--key")));
            File.WriteAllText(feed + ".sig", signature);
            Console.WriteLine($"Wrote {feed}.sig.");
            break;
        }
        case "verify":
        {
            var feed = Need("--feed");
            var config = UpdateConfiguration.Create("https://example.invalid/update.json", Need("--public"))
                ?? throw new ArgumentException("--public isn't a P-256 public key.");
            var (parsed, problem) = UpdateFeeds.Read(File.ReadAllBytes(feed), File.ReadAllText(feed + ".sig"), config.PublicKey);
            Console.WriteLine(problem ?? $"OK: Lunalyr {parsed!.Version}, {parsed.Size:N0} bytes, {parsed.Url}");
            return problem is null ? 0 : 1;
        }
        case "keygen":
        {
            var path = Path.GetFullPath(Need("--private"));
            if (File.Exists(path)) throw new IOException($"{path} exists; not overwriting a key.");
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Lunalyr.slnx"))) repo = repo.Parent;
            if (repo is not null && path.StartsWith(repo.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Keep the private key outside the repository.");
            var (privatePem, publicKey) = UpdateFeeds.NewKey();
            File.WriteAllText(path, privatePem);
            Console.WriteLine($"Private key: {path} (keep it secret and backed up).");
            Console.WriteLine($"Public key for the build (-p:LunalyrUpdateKey=...): {publicKey}");
            break;
        }
        default:
            Console.Error.WriteLine("Commands: make, feed, sign, verify, keygen. See docs/PACKAGING.md.");
            return 2;
    }
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or CryptographicException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
