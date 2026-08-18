using AzToMarkdown.Core;
using AzToMarkdown.Core.Abstractions;
using AzToMarkdown.Core.Azure;
using AzToMarkdown.Core.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

// ─────────────────────────────────────────────────────────────────────────────
// Argument parsing
// ─────────────────────────────────────────────────────────────────────────────
string? outputPath    = null;
string? subscription  = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--output"       when i + 1 < args.Length: outputPath   = args[++i]; break;
        case "--subscription" when i + 1 < args.Length: subscription = args[++i]; break;
        case "--help":
        case "-h":
            PrintUsage();
            return 0;
        default:
            // Fail loudly on unknown flags or a value-flag missing its argument, so e.g.
            // a forgotten `--output` value doesn't silently write to the default ./vault.
            AnsiConsole.MarkupLine($"[red]Unrecognized or incomplete argument:[/] '{Markup.Escape(args[i])}'. Use --help for usage.");
            return 1;
    }
}

// Default output to a "vault" folder in the current directory.
outputPath ??= Path.Combine(Directory.GetCurrentDirectory(), "vault");

// ─────────────────────────────────────────────────────────────────────────────
// DI setup
// ─────────────────────────────────────────────────────────────────────────────
var services = new ServiceCollection();
services.AddAzToMarkdownCore(subscription);
services.AddSingleton<IProgressReporter, SpectreProgressReporter>();

using var provider = services.BuildServiceProvider();

var reporter    = provider.GetRequiredService<IProgressReporter>();
var enumerator  = provider.GetRequiredService<TenantEnumerator>();
var extractor   = provider.GetRequiredService<RelationshipExtractor>();
var vaultWriter = provider.GetRequiredService<VaultWriter>();

// ─────────────────────────────────────────────────────────────────────────────
// Pre-flight
// ─────────────────────────────────────────────────────────────────────────────
AzCliQueryClient.CheckAzAvailable();
await AzCliQueryClient.EnsureLoggedInAsync();
await AzCliQueryClient.EnsureExtensionAsync("resource-graph");

// ─────────────────────────────────────────────────────────────────────────────
// Fetch → Build graph → Write vault
// ─────────────────────────────────────────────────────────────────────────────
AnsiConsole.MarkupLine("[bold cyan]AzToMarkdown[/] — tenant topology vault generator");
AnsiConsole.MarkupLine($"Output: [yellow]{outputPath}[/]");
AnsiConsole.WriteLine();

try
{
    // Step 1: Fetch tenant resources, resource groups, and role assignments (3 ARG queries)
    var (nodes, subNames) = await enumerator.FetchAllAsync();

    // Step 2: Build the in-memory directed graph (pure CPU, no I/O)
    reporter.Report("Building relationship graph…");
    var graph = extractor.Build(nodes);
    reporter.Report($"Graph: {graph.Nodes.Count} nodes, {CountEdges(graph)} edges.", ProgressLevel.Success);

    // Step 3: Write the Markdown vault
    vaultWriter.WriteAll(graph, subNames, outputPath);

    AnsiConsole.MarkupLine($"\n[bold green]Done.[/] Vault written to [yellow]{outputPath}[/]");
    AnsiConsole.MarkupLine("Open the folder as an Obsidian vault to navigate the topology.");
    return 0;
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[bold red]Error:[/] {ex.Message}");
    return 1;
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

static void PrintUsage()
{
    AnsiConsole.MarkupLine("""
        [bold cyan]AzToMarkdown[/] — maps an entire Azure tenant to an Obsidian Markdown vault.

        [bold]USAGE[/]
          AzToMarkdown [[OPTIONS]]

        [bold]OPTIONS[/]
          --output <path>        Root folder for the generated vault  [dim](default: ./vault)[/]
          --subscription <id>    Scope queries to a single subscription  [dim](default: all)[/]
          --help, -h             Show this help

        [bold]PREREQUISITES[/]
          • az CLI installed and authenticated (az login)
          • resource-graph extension installed (auto-installed if missing)

        [bold]EXAMPLE[/]
          AzToMarkdown --output C:\vaults\my-tenant
          AzToMarkdown --subscription 00000000-0000-0000-0000-000000000000
        """);
}

static int CountEdges(AzToMarkdown.Core.Models.TenantGraph graph)
{
    int count = 0;
    foreach (var node in graph.Nodes.Values)
        count += graph.GetOutbound(node.ResourceId).Count;
    return count;
}

// ─────────────────────────────────────────────────────────────────────────────
// Spectre progress reporter
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class SpectreProgressReporter : IProgressReporter
{
    public void Report(string message, ProgressLevel level = ProgressLevel.Info)
    {
        var markup = level switch
        {
            ProgressLevel.Success => $"[green]✔[/] {Markup.Escape(message)}",
            ProgressLevel.Warn    => $"[yellow]⚠[/] {Markup.Escape(message)}",
            ProgressLevel.Error   => $"[red]✖[/] {Markup.Escape(message)}",
            _                     => $"[grey]·[/] {Markup.Escape(message)}",
        };
        AnsiConsole.MarkupLine(markup);
    }
}
