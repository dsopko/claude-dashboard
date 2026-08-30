using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// The roster store and its two invariants (T1.25, issue #16 rules 4 and 6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Tested here rather than through the UI, because they are the store's.</strong> The
/// operator UI is T1.26 and will not be the only caller — the settings file is another today, and
/// an import would be a third. An invariant a caller has to remember to maintain is one the second
/// caller breaks, so every route in goes through <see cref="RosterBook"/> and every route is
/// covered by these.
/// </para>
/// </remarks>
public sealed class RosterBookTests
{
    /// <summary>A name in a second roster leaves the first, silently (rule 4).</summary>
    /// <remarks>
    /// Both halves are asserted. "It is in the new roster" alone would pass while the name was in
    /// both, which is the state rule 4 exists to make unrepresentable — and a name in two rosters
    /// would put one session in two groups, which the resolver cannot express either.
    /// </remarks>
    [Fact]
    public void A_name_added_to_a_second_roster_leaves_the_first()
    {
        var book = RosterBook.From([("orchestration", ["Director", "Coder"])])
            .With("docs", ["Coder"]);

        Assert.Equal("docs", book.RosterFor("Coder"));
        Assert.Equal("orchestration", book.RosterFor("Director"));

        // .ToArray() is load-bearing: ImmutableArray<string> equality is by underlying-array
        // REFERENCE, so a collection-expression "expected" compares unequal to an identical actual.
        // The same trap this codebase already hand-writes Equals for in Group and DashboardSettings.
        Assert.Equal(
            ["Director"],
            book.Rosters.Single(r => r.Name == "orchestration").Members.ToArray());
    }

    /// <summary>A roster emptied by a removal ceases to exist (rules 5 and 6).</summary>
    [Fact]
    public void A_roster_emptied_by_removal_ceases_to_exist()
    {
        var book = RosterBook.From([("solo", ["Director"])]).Without("Director");

        Assert.True(book.IsEmpty);
        Assert.Null(book.RosterFor("Director"));
    }

    /// <summary>
    /// <strong>A roster emptied because its last name moved elsewhere ceases to exist too.</strong>
    /// </summary>
    /// <remarks>
    /// The second route into rule 6, and the one that is easy to miss: nobody removed anything, so
    /// an implementation that only checked for emptiness on removal would leave an empty roster
    /// behind — and #16's whole reason for rule 6 is that there is then nothing to tidy up later
    /// and no empty roster to explain.
    /// </remarks>
    [Fact]
    public void A_roster_emptied_by_a_name_moving_away_ceases_to_exist()
    {
        var book = RosterBook.From([("solo", ["Director"])]).With("docs", ["Director"]);

        Assert.Equal("docs", Assert.Single(book.Rosters).Name);
        Assert.Equal("docs", book.RosterFor("Director"));
    }

    /// <summary>Emptying a roster by naming no members is rule 6 rather than a second operation.</summary>
    [Fact]
    public void A_roster_given_no_members_ceases_to_exist()
    {
        var book = RosterBook.From([("solo", ["Director"])]).With("solo", []);

        Assert.True(book.IsEmpty);
    }

    /// <summary>A session with no title is in no roster, and cannot be.</summary>
    /// <remarks>
    /// The alternative — matching "no title" against "no title" — would gather every unnamed
    /// session on the machine into one group, which is the one thing grouping must never invent.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_session_with_no_title_belongs_to_no_roster(string? title)
    {
        Assert.Null(RosterBook.From([("orchestration", ["Director"])]).RosterFor(title));
    }

    /// <summary>Matching is exact: a title that differs by case is a different name.</summary>
    /// <remarks>
    /// Stated as a test rather than left to the implementation because it is a judgement call.
    /// A roster's members are copied from titles the sessions themselves reported (T1.26 forms one
    /// by ticking live rows), so comparing exactly compares two copies of one string. Folding case
    /// would assert an equivalence nothing here has evidence for.
    /// </remarks>
    [Fact]
    public void Matching_is_exact()
    {
        var book = RosterBook.From([("orchestration", ["Director"])]);

        Assert.Equal("orchestration", book.RosterFor("Director"));
        Assert.Null(book.RosterFor("director"));
        Assert.Null(book.RosterFor("Director "));
    }

    /// <summary>
    /// <strong>A file can hold a name in two rosters; the store cannot.</strong>
    /// </summary>
    /// <remarks>
    /// The first roster in the order given keeps the name, which is what makes two loads of one
    /// file agree. The second is left with what remains — and disappears entirely if that is
    /// nothing, which is rule 6 applying to a correction rather than to an operator action.
    /// </remarks>
    [Fact]
    public void A_name_offered_to_two_rosters_stays_in_the_first()
    {
        var book = RosterBook.From(
        [
            ("orchestration", ["Director", "Coder"]),
            ("docs", ["Coder"]),
            ("empty-after-correction", ["Director"]),
        ]);

        Assert.Equal("orchestration", book.RosterFor("Coder"));
        Assert.Equal("orchestration", book.RosterFor("Director"));
        Assert.Equal("orchestration", Assert.Single(book.Rosters).Name);
    }

    /// <summary>Blank names, blank members and repeats are not representable.</summary>
    [Fact]
    public void Blanks_and_repeats_are_resolved_on_the_way_in()
    {
        var book = RosterBook.From(
        [
            ("  ", ["Director"]),
            ("  orchestration  ", ["  Director  ", "Director", "", "   ", "Coder"]),
        ]);

        var roster = Assert.Single(book.Rosters);

        Assert.Equal("orchestration", roster.Name);
        Assert.Equal(["Director", "Coder"], roster.Members.ToArray());
    }

    /// <summary>The same roster name twice is one roster, not two.</summary>
    [Fact]
    public void A_repeated_roster_name_merges()
    {
        var book = RosterBook.From([("orchestration", ["Director"]), ("orchestration", ["Coder"])]);

        Assert.Equal(["Director", "Coder"], Assert.Single(book.Rosters).Members.ToArray());
    }

    /// <summary>An empty book is the ordinary case and answers nothing.</summary>
    [Fact]
    public void An_empty_book_puts_nobody_in_a_roster()
    {
        Assert.True(RosterBook.Empty.IsEmpty);
        Assert.Empty(RosterBook.Empty.Rosters);
        Assert.Null(RosterBook.Empty.RosterFor("Director"));
    }

    /// <summary>The operations reject null rather than treating it as "no members".</summary>
    [Fact]
    public void The_operations_reject_null()
    {
        Assert.Throws<ArgumentNullException>(() => RosterBook.From(null!));
        Assert.Throws<ArgumentNullException>(() => RosterBook.Empty.With("x", null!));
    }
}
