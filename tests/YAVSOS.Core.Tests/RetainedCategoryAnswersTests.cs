using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using static YAVSOS.Core.Tests.ChestRuleEvaluatorTests;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class RetainedCategoryAnswersTests
{
    private static readonly CatalogSnapshot Before = CatalogSnapshot.Build(new[] { Item("Wood"), Item("Bread", type: "Consumable", health: 20, duration: 100) });
    private static readonly CatalogSnapshot After = CatalogSnapshot.Build(new[] { Item("Wood"), Item("Bread", type: "Consumable", health: 20, duration: 100), Item("NewItem") });

    private static RetainedCategoryAnswers Ready(out AuthoritativeAssignmentSnapshot snapshot)
    {
        var policy = TestPolicy(); var answers = new RetainedCategoryAnswers(); answers.SetScope(1, policy.Fingerprint);
        snapshot = Snapshot(Before, policy);
        Assert.Equal(CategoryAnswerUpdate.Replaced, answers.Accept(snapshot, Before, 1));
        return answers;
    }

    [Fact]
    public void MemberCount_CountsServerAnswersWithClosureAndClearsOnWithdrawal()
    {
        var answers = Ready(out _);
        Assert.Equal(1, answers.MemberCount("materials.wood")); Assert.Equal(1, answers.MemberCount("materials"));
        Assert.Equal(1, answers.MemberCount("food")); Assert.Equal(0, answers.MemberCount("weapons"));
        answers.Withdraw();
        Assert.Equal(0, answers.MemberCount("materials"));
    }

    [Fact]
    public void Rebuild_KeepsLastAnswersUntilTheNewResultPublishes()
    {
        var answers = Ready(out _); var generation = answers.Generation; var bread = answers.Resolve("Bread");
        answers.BeginRebuild();
        Assert.True(answers.Rebuilding); Assert.True(answers.CategoriesReady);
        Assert.Same(bread, answers.Resolve("Bread")); Assert.Equal(generation, answers.Generation);
        Assert.Null(answers.Resolve("NewItem"));
    }

    [Fact]
    public void IdenticalResult_DoesNotChangeGeneration()
    {
        var answers = Ready(out var first); var generation = answers.Generation;
        answers.BeginRebuild();
        var again = Snapshot(Before, TestPolicy());
        Assert.Equal(first.Fingerprint, again.Fingerprint);
        Assert.Equal(CategoryAnswerUpdate.Unchanged, answers.Accept(again, CatalogSnapshot.Build(new[] { Item("Wood"), Item("Bread", type: "Consumable", health: 20, duration: 100) }), 1));
        Assert.False(answers.Rebuilding); Assert.Equal(generation, answers.Generation);
    }

    [Fact]
    public void ChangedResult_ReplacesAnswersAndAddsNewItems()
    {
        var answers = Ready(out _); var generation = answers.Generation;
        answers.BeginRebuild();
        Assert.Equal(CategoryAnswerUpdate.Replaced, answers.Accept(Snapshot(After, TestPolicy()), After, 1));
        Assert.NotEqual(generation, answers.Generation); Assert.NotNull(answers.Resolve("NewItem"));
    }

    [Fact]
    public void SessionPolicyAndPublicationChanges_WithdrawImmediately()
    {
        var answers = Ready(out _); var policy = TestPolicy();
        answers.BeginRebuild(); answers.SetScope(2, policy.Fingerprint);
        Assert.False(answers.CategoriesReady); Assert.False(answers.Rebuilding); Assert.Null(answers.Resolve("Bread"));

        answers = Ready(out _); answers.SetScope(1, TestPolicy(2).Fingerprint);
        Assert.False(answers.CategoriesReady); Assert.Null(answers.Resolve("Wood"));

        answers = Ready(out _); var generation = answers.Generation; answers.Withdraw();
        Assert.False(answers.CategoriesReady); Assert.NotEqual(generation, answers.Generation);
    }

    [Fact]
    public void LateResults_FromAnOldSessionOrPolicy_AreRejected()
    {
        var answers = Ready(out var snapshot); answers.SetScope(2, TestPolicy().Fingerprint);
        Assert.Equal(CategoryAnswerUpdate.RejectedLate, answers.Accept(snapshot, Before, 1));
        Assert.False(answers.CategoriesReady);
        answers.SetScope(2, TestPolicy(2).Fingerprint);
        Assert.Equal(CategoryAnswerUpdate.RejectedLate, answers.Accept(snapshot, Before, 2));
        answers.SetScope(2, "");
        Assert.Equal(CategoryAnswerUpdate.RejectedLate, answers.Accept(Snapshot(Before, TestPolicy()), Before, 2));
    }

    [Fact]
    public void Rebuild_ThatNeverPublishes_KeepsTheAnswersUntilTheScopeChanges()
    {
        // D086 D7: a failed stage keeps the last answers; only a session or policy change withdraws them.
        var answers = Ready(out var snapshot);
        for (var i = 0; i < 100; i++) answers.BeginRebuild();
        Assert.True(answers.CategoriesReady); Assert.True(answers.Rebuilding);
        answers.SetScope(1, snapshot.PolicyFingerprint); Assert.True(answers.CategoriesReady);
        answers.SetScope(2, snapshot.PolicyFingerprint);
        Assert.False(answers.CategoriesReady); Assert.False(answers.Rebuilding);
    }
}
