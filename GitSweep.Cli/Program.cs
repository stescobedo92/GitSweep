using GitSweep.Cli.Commands;
using GitSweep.Core.Services;
using GitSweep.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;

Console.OutputEncoding = Encoding.UTF8;

var console = AnsiConsole.Create(new AnsiConsoleSettings
{
    Ansi = AnsiSupport.Yes,
    ColorSystem = ColorSystemSupport.TrueColor,
    Interactive = InteractionSupport.Yes,
});

AnsiConsole.Console = console;

var app = new CommandApp<CleanCommand>(new TypeRegistrar(console));

app.Configure(config =>
{
    config.SetApplicationName("gitsweep");
    config.SetApplicationVersion(typeof(CleanCommand).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");
    config.CaseSensitivity(CaseSensitivity.None);
    config.UseStrictParsing();
    config.SetExceptionHandler((ex, resolver) =>
    {
        var ansiConsole = resolver?.Resolve(typeof(IAnsiConsole)) as IAnsiConsole ?? console;
        ansiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
        return 1;
    });
    config.AddExample("--dry-run");
    config.AddExample("clean", "--all", "--yes", "--merged-only");
    config.AddCommand<CleanCommand>("clean")
        .WithDescription("Scan and clean stale or merged local Git branches.")
        .WithExample("clean", "-a", "3", "-p", "./my-repo")
        .WithExample("clean", "--dry-run")
        .WithExample("clean", "--all", "--yes", "--merged-only");
});

return await app.RunAsync(args);

// Type declarations must come after top-level statements
internal sealed class TypeRegistrar : ITypeRegistrar
{
    private readonly IServiceCollection _services;

    public TypeRegistrar(IAnsiConsole console)
    {
        _services = new ServiceCollection();
        _services.AddSingleton<IGitRepositoryService, GitRepositoryService>();
        _services.AddSingleton<IBranchAnalyzer, BranchAnalyzer>();
        _services.AddSingleton(console);
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
    public object? Resolve(Type? type) => type is null ? null : _serviceProvider.GetService(type);
    public void Dispose() => (_serviceProvider as IDisposable)?.Dispose();
}
