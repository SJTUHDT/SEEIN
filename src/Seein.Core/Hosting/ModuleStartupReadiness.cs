namespace Seein.Core.Hosting;

public static class ModuleStartupReadiness
{
    public static bool IsSettled(IEnumerable<string> expected, ModuleHealthResult health) =>
        !health.Starting && expected.All(id => health.Modules.Any(module => module.Id == id && (module.Ready || module.Error is not null)));
}
