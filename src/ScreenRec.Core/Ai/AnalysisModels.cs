using System.Text.Json.Serialization;

namespace ScreenRec.Core.Ai;

/// <summary>One step as the AI understood it.</summary>
public sealed class StepDescription
{
    public int Index { get; set; }
    public double Start { get; set; }
    public string Kind { get; set; } = "";
    public string? App { get; set; }
    /// <summary>Imperative instruction a trainee could follow ("Click Post in the Journal Entry toolbar").</summary>
    public string Action { get; set; } = "";
    /// <summary>The UI element acted on, by its visible label.</summary>
    public string? Target { get; set; }
    /// <summary>What the expert was trying to achieve with this step.</summary>
    public string? Why { get; set; }
    /// <summary>What changed on screen as a result.</summary>
    public string? Result { get; set; }
    /// <summary>Values entered or read that matter (with sensitive data generalized).</summary>
    public List<string> DataUsed { get; set; } = new();
    /// <summary>A judgement call the expert made here, if any — the part a trainee most needs.</summary>
    public string? Decision { get; set; }
    public double Confidence { get; set; }
    public string? BeforeFrame { get; set; }
    public string? AfterFrame { get; set; }
    public string? Note { get; set; }
}

public sealed class ProcedureStep
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string? Why { get; set; }
    public string? Check { get; set; }
    public List<int> SourceSteps { get; set; } = new();
    public string? Screenshot { get; set; }
}

public sealed class DecisionRule
{
    public string When { get; set; } = "";
    public string Then { get; set; } = "";
}

public sealed class SessionAnalysis
{
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    /// <summary>The business goal of the whole recording.</summary>
    public string Goal { get; set; } = "";
    public List<string> Applications { get; set; } = new();
    public List<string> Inputs { get; set; } = new();
    public List<string> Outputs { get; set; } = new();
    public List<ProcedureStep> Procedure { get; set; } = new();
    public List<DecisionRule> Decisions { get; set; } = new();
    public List<string> Pitfalls { get; set; } = new();
    public List<string> Glossary { get; set; } = new();
    /// <summary>Questions only the expert can answer (gaps the recording didn't show).</summary>
    public List<string> OpenQuestions { get; set; } = new();
    /// <summary>A self-contained runbook a Guardian desktop automation can execute.</summary>
    public string AutomationPrompt { get; set; } = "";
    /// <summary>A cron expression when the work looked periodic, else null (run on demand).</summary>
    public string? SuggestedCron { get; set; }
    /// <summary>How automatable the task is, 0..1, with the reason.</summary>
    public double Automatability { get; set; }
    public string? AutomatabilityReason { get; set; }
    public List<StepDescription> Steps { get; set; } = new();
    public string Model { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public List<string> Warnings { get; set; } = new();

    [JsonIgnore] public bool HasProcedure => Procedure.Count > 0;
}

/// <summary>Everything learned about one expert across their recordings: what Guardian gets as the expert's skill.</summary>
public sealed class ExpertProfile
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Overview { get; set; } = "";
    public List<string> Responsibilities { get; set; } = new();
    public List<string> Applications { get; set; } = new();
    public List<ExpertTask> Tasks { get; set; } = new();
    public List<DecisionRule> Rules { get; set; } = new();
    public List<string> Pitfalls { get; set; } = new();
    public List<string> Glossary { get; set; } = new();
    public List<string> OpenQuestions { get; set; } = new();
    public List<string> SessionIds { get; set; } = new();
    public string Model { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class ExpertTask
{
    public string Name { get; set; } = "";
    public string When { get; set; } = "";
    public string Summary { get; set; } = "";
    public string SessionId { get; set; } = "";
}
