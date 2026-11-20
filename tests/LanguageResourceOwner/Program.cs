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
