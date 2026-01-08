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
    private readonly IServiceCollection _services;

    public TypeRegistrar()
    {
        _services = new ServiceCollection();
        _services.AddSingleton<IGitRepositoryService, GitRepositoryService>();
        _services.AddSingleton<IBranchAnalyzer, BranchAnalyzer>();
    }

    public ITypeResolver Build()
    {
        var provider = _services.BuildServiceProvider();
        return new TypeResolver(provider);
    }

    public void Register(Type service, Type implementation)
    {
        _services.AddSingleton(service, implementation);
    }

    void ITypeRegistrar.RegisterInstance(Type service, object implementation)
    {
        _services.AddSingleton(service, implementation);
    }

    void ITypeRegistrar.RegisterLazy(Type service, Func<object> func)
    {
        _services.AddSingleton(service, _ => func());
    }
}

internal sealed class TypeResolver : ITypeResolver
{
    private readonly IServiceProvider _serviceProvider;
    public TypeResolver(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;
    public object Resolve(Type? type) => _serviceProvider.GetRequiredService(type!);
    public void Dispose() => (_serviceProvider as IDisposable)?.Dispose();
}
