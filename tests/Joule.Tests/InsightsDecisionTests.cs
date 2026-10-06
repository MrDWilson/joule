using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

// The Insights page's small decisions: done by hand (writes off), reopen, and undo "applied".
public sealed class InsightsDecisionTests
{
    static AppState State() => new()
    {
        DataSource = "Live",
        Proposals =
        [
            new Proposal { Id = "p1", Title = "Lower house load scaling", Status = "Pending", Changes = [new Change("load_scaling", "1.08", "1.00")] },
            new Proposal { Id = "p2", Title = "Combine charge slots", Status = "Denied", DecidedAt = DateTimeOffset.UtcNow, DecisionNote = "Keep it off" },
            new Proposal { Id = "p3", Title = "Applied one", Status = "Applied" },
        ],
        Investigations =
        [
            new Investigation
            {
                Id = "i1", Title = "Export isn't compared with the plan", DismissedAt = DateTimeOffset.UtcNow, DecisionNote = "Not now",
                NextSteps = [new InvestigationNextStep { Id = "s1", Title = "Check the EV meter", Status = "closed", ClosedAt = DateTimeOffset.UtcNow, ClosedReason = "Dismissed by user", DecidedAt = DateTimeOffset.UtcNow, DecisionNote = "done" }],
                FileChanges =
                [
                    new ConfigFileChange { Id = "f1", File = "apps.yaml", Summary = "Add export_today", Status = "applied", AppliedAt = DateTimeOffset.UtcNow },
                    new ConfigFileChange { Id = "f2", File = "apps.yaml", Summary = "Use !secret", Status = "dismissed", ClosedAt = DateTimeOffset.UtcNow, ClosedReason = "Dismissed by user", DecisionNote = "later" },
                ],
            },
        ],
    };

    [Fact]
    public void MarkingASuggestionDoneClosesItWithoutTouchingSettings()
    {
        var s = State();
        InsightsDecisions.MarkProposalDone(s, "p1");
        var p = s.Proposals[0];
        Assert.Equal("Done", p.Status);
        Assert.NotNull(p.DecidedAt);
        Assert.Empty(s.Revisions);
        Assert.Contains(s.Activities, a => a.Message.Contains("in Predbat yourself", StringComparison.Ordinal));
        Assert.Throws<DomainException>(() => InsightsDecisions.MarkProposalDone(s, "p1"));
        Assert.Equal(404, Assert.Throws<DomainException>(() => InsightsDecisions.MarkProposalDone(s, "missing")).Status);
    }

    [Fact]
    public void DeclinedOrDoneSuggestionsReopenButAppliedOnesDoNot()
    {
        var s = State();
        InsightsDecisions.ReopenProposal(s, "p2");
        Assert.Equal(("Pending", (DateTimeOffset?)null, (string?)null), (s.Proposals[1].Status, s.Proposals[1].DecidedAt, s.Proposals[1].DecisionNote));
        InsightsDecisions.MarkProposalDone(s, "p1");
        InsightsDecisions.ReopenProposal(s, "p1");
        Assert.Equal("Pending", s.Proposals[0].Status);
        Assert.Throws<DomainException>(() => InsightsDecisions.ReopenProposal(s, "p3"));
        Assert.Throws<DomainException>(() => InsightsDecisions.ReopenProposal(s, "p1"));
    }

    [Fact]
    public void ClosedToDosAndFileEditsReopenAsNew()
    {
        var s = State();
        InsightsDecisions.ReopenFollowUp(s, "i1", "s1");
        var step = s.Investigations[0].NextSteps[0];
        Assert.Equal("open", step.Status);
        Assert.Null(step.ClosedAt); Assert.Null(step.ClosedReason); Assert.Null(step.DecidedAt); Assert.Null(step.DecisionNote);
        Assert.Throws<DomainException>(() => InsightsDecisions.ReopenFollowUp(s, "i1", "s1"));

        InsightsDecisions.ReopenFileChange(s, "i1", "f2");
        var change = s.Investigations[0].FileChanges[1];
        Assert.Equal("pending", change.Status);
        Assert.Null(change.ClosedReason); Assert.Null(change.DecisionNote);
        Assert.Throws<DomainException>(() => InsightsDecisions.ReopenFileChange(s, "i1", "f1"));
    }

    [Fact]
    public void ApplyingAFileEditCanBeTakenBack()
    {
        var s = State();
        InsightsDecisions.UnmarkApplied(s, "i1", "f1");
        var change = s.Investigations[0].FileChanges[0];
        Assert.Equal(("pending", (DateTimeOffset?)null), (change.Status, change.AppliedAt));
        Assert.Throws<DomainException>(() => InsightsDecisions.UnmarkApplied(s, "i1", "f1"));
    }

    sealed class Failing(int status) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent("{\"error\":{\"message\":\"model not found\",\"code\":\"model_not_found\"}}") });
        }
    }
    static AiModelClient Client(HttpMessageHandler handler, string directory)
    {
        var http = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "fixture-key", ["Ai:ApiBaseUrl"] = "https://ai.example.test/v1" }).Build();
        return new AiModelClient(http, new ChatGptAuth(http, Path.Combine(directory, "auth")), config) { RetryDelays = [TimeSpan.Zero] };
    }

    [Fact]
    public async Task ConnectionTestChecksTheChoiceAndReportsTheProvidersReasonWithoutRetrying()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joule-test-" + Guid.NewGuid().ToString("N"));
        var handler = new Failing(404);
        var model = Client(handler, directory);
        Assert.True((await InsightsEndpoints.TestAiAsync(new("Demo", ""), model, demo: true, default)).Ok);
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => InsightsEndpoints.TestAiAsync(new("Demo", ""), model, demo: false, default))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => InsightsEndpoints.TestAiAsync(new("Api", " "), model, demo: false, default))).Status);
        var result = await InsightsEndpoints.TestAiAsync(new("Api", "fixture-model"), model, demo: false, default);
        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void DismissedFindingsReopen()
    {
        var s = State();
        InsightsDecisions.ReopenFinding(s, "i1");
        Assert.Null(s.Investigations[0].DismissedAt);
        Assert.Null(s.Investigations[0].DecisionNote);
        Assert.Throws<DomainException>(() => InsightsDecisions.ReopenFinding(s, "i1"));
    }
}
