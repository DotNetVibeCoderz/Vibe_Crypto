using System.Text.Json;
using Spectre.Console;

namespace Crypto.Net.Cli;

/// <summary>Console rendering helpers: rich output for humans, JSON for scripts.</summary>
internal static class Ui
{
    public const string Attribution = "Built by Gravicode Studios · led by Kang Fadhil";
    public const string AttributionId = "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static readonly Color Accent = new(0, 229, 160);   // mint
    public static readonly Color Accent2 = new(255, 176, 32); // amber
    public static readonly Color Muted = Color.Grey;

    public static void Banner()
    {
        AnsiConsole.Write(new FigletText("Crypto.Net").Color(Accent));
        AnsiConsole.MarkupLine($"[grey]Multi-chain crypto toolkit for .NET 10 + Rust  ·  [/][#{Accent2.ToHex()}]{Attribution}[/]");
        AnsiConsole.WriteLine();
    }

    public static void Json(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    public static Table Table(params string[] columns)
    {
        var t = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35);
        foreach (var c in columns) t.AddColumn(new TableColumn($"[bold]{Markup.Escape(c)}[/]"));
        return t;
    }

    public static void Section(string title) =>
        AnsiConsole.Write(new Rule($"[bold #{Accent.ToHex()}]{Markup.Escape(title)}[/]").LeftJustified().RuleStyle("grey35"));

    public static void Success(string message) => AnsiConsole.MarkupLine($"[#{Accent.ToHex()}]✔[/] {Markup.Escape(message)}");
    public static void Warn(string message) => AnsiConsole.MarkupLine($"[#{Accent2.ToHex()}]![/] {Markup.Escape(message)}");
    public static void Error(string message) => AnsiConsole.MarkupLine($"[red]✖ {Markup.Escape(message)}[/]");

    public static void SecretPanel(string title, string secret, string warning)
    {
        var panel = new Panel(new Markup($"[bold white]{Markup.Escape(secret)}[/]\n\n[#{Accent2.ToHex()}]{Markup.Escape(warning)}[/]"))
            .Header($"[bold #{Accent2.ToHex()}] {Markup.Escape(title)} [/]")
            .Border(BoxBorder.Double)
            .BorderColor(Accent2)
            .Padding(1, 0);
        AnsiConsole.Write(panel);
    }

    public static string Secret(string prompt) =>
        AnsiConsole.Prompt(new TextPrompt<string>($"[#{Accent.ToHex()}]{Markup.Escape(prompt)}[/]").Secret());
}
