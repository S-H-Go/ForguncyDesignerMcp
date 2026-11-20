using ForguncyDesignerMcp.Adapters.V12;
using ForguncyDesignerMcp.Core.Platform;

static void Check(bool condition, string scenario)
{
    if (!condition) throw new InvalidOperationException(scenario);
}

var leaf = new ModuleAddress(ModuleKind.LanguageResource, "Demo_Header");
var application = new ModuleAddress(ModuleKind.ApplicationSettings, "Current");
Check(V12ProjectAdapter.RegisteredApplicationOwner(leaf.Kind) == application, "language leaf resolves to exact native root");
Check(V12ProjectAdapter.RegisteredApplicationOwner(ModuleKind.GlobalVariable) == application, "existing global-variable route is retained");
Check(V12ProjectAdapter.RegisteredApplicationOwner(ModuleKind.Report) is null, "unrelated modules must not gain an application tracking route");

const string owner = "ApplicationSetting";
const string file = "ApplicationSettings.json";
bool registered = false;
NativeCollaboration.EditRegistration Witness(string name, params string[] files) =>
    new(name, files.ToHashSet(StringComparer.OrdinalIgnoreCase), () => registered = true);
bool Covers(NativeCollaboration.EditRegistration witness, ModuleKind kind = ModuleKind.LanguageResource) =>
    NativeModules.CoversReviewedApplicationTracking(kind, owner, file, witness);

Check(Covers(Witness(owner, file)), "reviewed exact owner inventory supports read-only preflight");
Check(!Covers(Witness("Theme", file)), "different native owner cannot authorize the language update");
Check(!Covers(Witness(owner, "Other.json")), "missing required file does not authorize the language update");
Check(!Covers(Witness(owner, file), ModuleKind.Report), "unrelated kinds cannot reuse the application witness");
Check(!registered, "read-only tracking validation must not activate the registration callback");
Console.WriteLine("PASS V12 LanguageResource canonical owner and review-only tracking");

const string favicon = "favicon.ico";
var sandbox = Path.Combine(Path.GetTempPath(), "mcp-save-evidence-" + Guid.NewGuid().ToString("N"));
var staging = Path.Combine(sandbox, "staging");
var source = Path.Combine(sandbox, "source");
Directory.CreateDirectory(staging);
Directory.CreateDirectory(source);
File.WriteAllText(Path.Combine(source, favicon), "synthetic source");
Check(NativeSaveVerification.FirstStagedSourceMismatch(staging, source, [favicon]) == (favicon, "StagedFileMissing"), "missing staged companion");
File.WriteAllText(Path.Combine(staging, favicon), "synthetic staging");
Check(NativeSaveVerification.FirstStagedSourceMismatch(staging, source, [favicon]) == (favicon, "StagingSourceMismatch"), "mismatched companion");
File.WriteAllText(Path.Combine(staging, favicon), "synthetic source");
Check(NativeSaveVerification.FirstStagedSourceMismatch(staging, source, [favicon]) is null, "matching companion stays valid");
Check(NativeSaveVerification.ApplicationFileRole("Other/private.bin") == "applicationCompanion", "unreviewed paths are never exposed in the diagnostic role");
Check(NativeSaveVerification.ApplicationFileRole(favicon) == favicon, "favicon displays only a fixed role");
Console.WriteLine("PASS V12 fixed companion roles and staging/source diagnostics");
