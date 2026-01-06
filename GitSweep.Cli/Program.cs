using GitSweep.Cli.Commands;
using GitSweep.Core.Services;
using GitSweep.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

var app = new CommandApp(new TypeRegistrar());

app.Configure(config =>
{
    config.SetApplicationName("gitsweep");
    config.AddCommand<CleanCommand>("clean")
        .WithDescription("Scan and clean stale or merged local Git branches.")
        .WithExample("gitsweep clean -a 3 -p ./my-repo");
});

return await app.RunAsync(args);

// Type declarations must come after top-level statements
internal sealed class TypeRegistrar : ITypeRegistrar
{
    private readonly IServiceProvider _serviceProvider;

    public TypeRegistrar()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitRepositoryService, GitRepositoryService>();
        services.AddSingleton<IBranchAnalyzer, BranchAnalyzer>();
        _serviceProvider = services.BuildServiceProvider();
    }

    public ITypeResolver Build() => new TypeResolver(_serviceProvider);

    public void Register(Type service, Type implementation) { /* Not used in this simple setup */ }
    void ITypeRegistrar.RegisterInstance(Type service, object implementation) { /* Not used */ }
    void ITypeRegistrar.RegisterLazy(Type service, Func<object> func) { /* Not used */ }
}

internal sealed class TypeResolver : ITypeResolver
{
    private readonly IServiceProvider _serviceProvider;
    public TypeResolver(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;
    public object Resolve(Type? type) => _serviceProvider.GetRequiredService(type!);
    public void Dispose() => (_serviceProvider as IDisposable)?.Dispose();
}
