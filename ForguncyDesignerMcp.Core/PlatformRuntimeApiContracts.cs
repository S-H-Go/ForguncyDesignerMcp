using System.Text.Json;

namespace ForguncyDesignerMcp.Core.Platform;

// Mixed project/runtime operations (for example native front-end library
// packages) retain separate project-save and runtime synchronization evidence.
public sealed record RuntimeApiReceipt(string OperationId,string State,string Code,string Message,
    bool? NativeAccepted,bool RegistryVerified,bool ProjectSaveAccepted,bool ProjectSourceVerified,
    bool ProjectDirtyFalse,bool RuntimeFileSynchronized,bool RequiresRegenerate,bool ExecutionMayContinue,
    JsonElement? NativeResponse,string? Limitation);

public interface IProjectRuntimeApiAdapter
{
    Task ValidateRuntimeApiAsync(PlatformApiRequest request,CancellationToken cancellationToken=default);
    Task<RuntimeApiReceipt> InvokeRuntimeApiAsync(PlatformApiRequest request,CancellationToken cancellationToken=default);
}

public static class RuntimeApiSchemas
{
    public static JsonElement Receipt()=>PlatformJson.Element(new
    {
        type="object",additionalProperties=false,
        properties=new
        {
            operationId=new{type="string",format="uuid"},state=new{type="string"},code=new{type="string"},message=new{type="string"},
            nativeAccepted=new{type=new[]{"boolean","null"}},registryVerified=new{type="boolean"},
            projectSaveAccepted=new{type="boolean"},projectSourceVerified=new{type="boolean"},projectDirtyFalse=new{type="boolean"},
            runtimeFileSynchronized=new{type="boolean"},requiresRegenerate=new{type="boolean"},executionMayContinue=new{type="boolean"},
            nativeResponse=new{type=new[]{"object","array","null"}},limitation=new{type=new[]{"string","null"}}
        },
        required=new[]{"operationId","state","code","message","nativeAccepted","registryVerified","projectSaveAccepted","projectSourceVerified",
            "projectDirtyFalse","runtimeFileSynchronized","requiresRegenerate","executionMayContinue","nativeResponse","limitation"},
        description="Native mixed runtime/project lifecycle. Project save/source proof, registry readback and preview/runtime file synchronization are independent results. Native acceptance does not imply runtime activation or generation."
    });
}
