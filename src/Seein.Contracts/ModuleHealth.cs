using Seein.Contracts;
namespace Seein.Core;
public sealed record ModuleRuntimeStatus(string Id,bool Ready,string? Error=null);
public sealed record ModuleHealthRequest(string? RequestId=null);
public sealed record ModuleHealthResult(bool Starting,IReadOnlyList<ModuleRuntimeStatus> Modules,string? RequestId=null);
