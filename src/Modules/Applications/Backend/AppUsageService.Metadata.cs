using Seein.Core;
namespace Seein.Service;
public sealed partial class AppUsageService
{
    public async Task<AppMetaResult> ApplyMetaAsync(AppMetaRequest request, CancellationToken token)
    {
        await EnsureInitializedAsync(token).ConfigureAwait(false);
        try
        {
            switch (request.Kind)
            {
                case "rename":
                    if (string.IsNullOrWhiteSpace(request.Value)) return new AppMetaResult(false, "名称不能为空", request.RequestId);
                    await _repository.SetDisplayNameAsync(request.AppKey, request.Value.Trim(), token).ConfigureAwait(false);
                    break;
                case "hide":
                    await _repository.SetHiddenAsync(request.AppKey, true, token).ConfigureAwait(false);
                    break;
                case "show":
                    await _repository.SetHiddenAsync(request.AppKey, false, token).ConfigureAwait(false);
                    break;
                case "color":
                    await _repository.SetColorAsync(request.AppKey,
                        string.IsNullOrWhiteSpace(request.Value) ? null : request.Value.Trim(), token).ConfigureAwait(false);
                    break;
                case "icon":
                    await _repository.SetIconPathAsync(request.AppKey,
                        string.IsNullOrWhiteSpace(request.Value) ? null : request.Value.Trim(), token).ConfigureAwait(false);
                    break;
                case "unmerge":
                    await _repository.SetMergedIntoAsync(request.AppKey, null, token).ConfigureAwait(false);
                    break;
                case "asapp":
                    await _repository.SetSystemOverrideAsync(request.AppKey, false, token).ConfigureAwait(false);
                    break;
                case "assystem":
                    await _repository.SetSystemOverrideAsync(request.AppKey, true, token).ConfigureAwait(false);
                    break;
                case "autosystem":
                    await _repository.SetSystemOverrideAsync(request.AppKey, null, token).ConfigureAwait(false);
                    break;
                case "merge":
                    await _repository.SetMergedIntoAsync(request.AppKey,
                        string.IsNullOrWhiteSpace(request.Value) ? null : request.Value, token).ConfigureAwait(false);
                    break;
                default:
                    return new AppMetaResult(false, $"未知操作：{request.Kind}", request.RequestId);
            }
            return new AppMetaResult(true, null, request.RequestId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to apply application metadata change {Kind}", request.Kind);
            return new AppMetaResult(false, ex.Message, request.RequestId);
        }
    }
}
