using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Compositions.StageLines;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>
/// Torque's phrase choreography (the turning column), without rendering: faces by phrase, rows by grammar, roles, the
/// column's turn, and the poses words take as they arrive (the Mac's TorqueLayoutTests, same inputs and values).
/// </summary>
public class TorqueLayoutTests
{
    private static (TorquePlan Plan, List<StageRun> Runs) Plan(IReadOnlyList<LyricWord> words, IReadOnlyList<bool>? glue = null)
    {
        var runs = Runs(words, glue);
        return (TorqueGeometry.MakePlan(runs, StagePhrases.Analyse(runs)), runs);
    }

    private static string[] Text(int[][] rows, List<StageRun> runs) =>
        [.. rows.Select(row => string.Join(" ", row.Select(id => runs[id].Text)))];

    // Ported from TorqueLayoutTests.swift: testFacesAndRowsFollowThePhrases
    [Fact]
    public void FacesAndRowsFollowThePhrases()
    {
        var (plan, runs) = Plan(Mixed);
        Assert.Equal(3, plan.Faces.Length);
        Assert.Equal(["and we keep", "running"], Text(plan.Faces[0].Rows, runs));
        Assert.Equal(["through the city", "lights"], Text(plan.Faces[1].Rows, runs));
        Assert.Equal(["until the morning", "comes"], Text(plan.Faces[2].Rows, runs));
        Assert.Equal(TorqueRole.Focal, plan.Roles[3]);
        Assert.Equal(TorqueRole.Function, plan.Roles[5]);
        Assert.Equal(TorqueRole.Content, plan.Roles[6]);
        Assert.Equal(3, plan.Roles.Count(role => role == TorqueRole.Focal)); // One focal word a face
    }

    // Ported from TorqueLayoutTests.swift: testRowsPutTheFocalGroupOnItsOwnRow
    [Fact]
    public void RowsPutTheFocalGroupOnItsOwnRow()
    {
        int[] function = [0, 3];
        // "the long road home": the focal word in the middle takes the middle row.
        Assert.Equal([[0, 1], [2], [3, 4]], TorqueGeometry.Rows([0, 1, 2, 3, 4], 2, function.Contains));
        // A trailing function word joins the word before it.
        Assert.Equal([[0, 1], [2, 3]], TorqueGeometry.Rows([0, 1, 2, 3], 1, id => id is 3 or 0));
        Assert.Equal([[7]], TorqueGeometry.Rows([7], 7, _ => true));
    }

    // Ported from TorqueLayoutTests.swift: testNeighbouringFacesAlternate
    [Fact]
    public void NeighbouringFacesAlternate()
    {
        var (plan, _) = Plan(Mixed);
        for (var i = 1; i < plan.Faces.Length; i++) Assert.NotEqual(plan.Faces[i - 1].FlushTrailing, plan.Faces[i].FlushTrailing);
    }

    // Ported from TorqueLayoutTests.swift: testRunsShareAClockAndHeldWordsTighten
    [Fact]
    public void RunsShareAClockAndHeldWordsTighten()
    {
        var (plan, runs) = Plan(Mixed);
        Assert.Equal(runs[0].Start, plan.Clocks[0]);
        Assert.Equal(runs[0].Start, plan.Clocks[1]);
        Assert.Equal(runs[0].Start, plan.Clocks[2]); // keep rises with and/we
        Assert.Equal(runs[4].Start, plan.Clocks[4]);
        Assert.True(plan.Held[3] > 1, "running is held");
        var running = runs[3];
        double Depth(double t) => TorqueGeometry.Pose(running, TorqueRole.Focal, running.Start, plan.Held[3], false, t, false).Depth;
        Assert.True(Depth(running.Start + 0.5) < Depth(running.Start + 1.2));
        Assert.Equal(Depth(running.Start + 2), Depth(running.Start + 4)); // Holds still once sung
    }

    // Ported from TorqueLayoutTests.swift: testSignatureMoveBelongsToTheFocalWord
    [Fact]
    public void SignatureMoveBelongsToTheFocalWord()
    {
        var (plan, runs) = Plan(Mixed);
        TorquePose Pose(int id, double dt) =>
            TorqueGeometry.Pose(runs[id], plan.Roles[id], plan.Clocks[id], plan.Held[id], false, runs[id].Start + dt, false);
        Assert.True(Pose(3, 0).Wring > 30, "running arrives wrung");
        Assert.True(Pose(6, 0).Wring < Pose(3, 0).Wring * 0.6, "city is wrung less far");
        Assert.True(Pose(6, 0).Rise > 0, "city rises");
        Assert.True(Pose(3, 0).Scale > 1.1, "running lands with a punch");
        Assert.Equal(1, Pose(6, 0).Scale);
        Assert.Equal(0, Pose(5, 0).Wring);
        Assert.Equal(0, Pose(5, 0).Rise); // the only appears
        foreach (var id in new[] { 3, 5, 6 })
        {
            Assert.Equal(0, Pose(id, 1.5).Wring, 1e-9);
            Assert.Equal(0, Pose(id, 1.5).Rise, 1e-9);
            Assert.Equal(1, Pose(id, 1.5).Scale, 1e-9);
        }
        // Not before its beat; at once under Reduce Motion.
        Assert.Equal(0, Pose(6, -0.1).Opacity);
        Assert.Equal(TorquePose.Rest, TorqueGeometry.Pose(runs[3], TorqueRole.Focal, runs[3].Start, 0, false, runs[3].Start, true));
    }

    // Ported from TorqueLayoutTests.swift: testColumnTurnsSmoothlyAndShowsAtMostTwoFaces
    [Fact]
    public void ColumnTurnsSmoothlyAndShowsAtMostTwoFaces()
    {
        var (plan, runs) = Plan(Long);
        Assert.True(plan.Faces.Length > 3);
        double? previous = null;
        for (var t = runs[0].Start - 0.2; t < runs[^1].Start + 1; t += 1.0 / 240)
        {
            var rho = TorqueGeometry.Rotation(plan, t, 0, false);
            if (previous is { } p) Assert.True(Math.Abs(rho - p) < 0.06, $"The column jumped at {t}");
            previous = rho;
            Assert.True(Enumerable.Range(0, plan.Faces.Length).Count(k => Math.Abs(k - rho) < 0.999) <= 2, $"Too many faces at {t}");
        }
        for (var index = 0; index < plan.Faces.Length; index++)
        {
            var face = plan.Faces[index];
            // Facing the phrase (within the overshoot) a moment after its first word.
            Assert.Equal(index, TorqueGeometry.Rotation(plan, face.Start + 0.25, 0, false), 0.12);
            if (index > 0)
            {
                Assert.True(face.TurnStart <= face.Start - StageMotion.Lead);
                Assert.True(face.TurnStart > plan.Faces[index - 1].Runs.Max(id => runs[id].Start));
            }
        }
        // Reduce Motion steps to the newest word's face.
        Assert.Equal(plan.Faces.Length - 1, TorqueGeometry.Rotation(plan, 99, runs.Count, true));
    }

    // Ported from TorqueLayoutTests.swift: testLineEntersAndLeavesTurningTheSameWay
    [Fact]
    public void LineEntersAndLeavesTurningTheSameWay()
    {
        var (plan, runs) = Plan(Mixed);
        var first = runs[0].Start - StageMotion.Lead;
        Assert.Equal(45, TorqueGeometry.Swing(plan, first, double.PositiveInfinity, false), 1e-9); // Enters already turning
        Assert.Equal(0, TorqueGeometry.Swing(plan, first + 1, double.PositiveInfinity, false), 1e-9);
        const double exit = 8.0;
        Assert.True(TorqueGeometry.Swing(plan, exit + 0.2, exit, false) < 0, "Leaves turning on to the left");
        Assert.Equal(1, TorqueGeometry.Leaving(exit + 1, exit));
        Assert.Equal(0, TorqueGeometry.Swing(plan, exit + 0.2, exit, true));
    }

    // Ported from TorqueLayoutTests.swift: testLineTimedLineIsOneFace
    [Fact]
    public void LineTimedLineIsOneFace()
    {
        var (plan, _) = Plan([.. "rain over the city can fly away tonight".Split(' ').Select(text => new LyricWord(text, 2, 6))]);
        Assert.True(plan.LineTimed);
        Assert.Single(plan.Faces);
        Assert.True(plan.Faces[0].Rows.Length > 1);
    }

    // Ported from TorqueLayoutTests.swift: testJapaneseRunsStayWhole
    [Fact]
    public void JapaneseRunsStayWhole()
    {
        var (plan, runs) = Plan(Japanese, JapaneseGlue);
        Assert.Equal(Enumerable.Range(0, runs.Count), plan.Faces.SelectMany(face => face.Runs));
        Assert.Equal(Enumerable.Range(0, runs.Count), plan.Faces.SelectMany(face => face.Rows.SelectMany(row => row)));
    }

    // Ported from TorqueLayoutTests.swift: testFaceLayoutAndTurn
    [Fact]
    public void FaceLayoutAndTurn()
    {
        SizeD[] sizes = [new(100, 60), new(80, 40), new(300, 120)];
        double[] baselines = [48, 32, 96];
        int[][] rows = [[0, 1], [2]];
        var left = TorqueGeometry.ArrangeFace(sizes, baselines, rows, trailing: false, rightToLeft: false, wordGap: 10, rowGap: 0);
        Assert.Equal(300, left.Size.Width);
        Assert.Equal(0, left.Frames[0].MinX);
        Assert.Equal(left.Frames[1].MinY + 32, left.Frames[0].MinY + 48, 1e-9); // Shared baseline
        var right = TorqueGeometry.ArrangeFace(sizes, baselines, rows, trailing: true, rightToLeft: true, wordGap: 10, rowGap: 0);
        Assert.Equal(300, right.Frames[0].MaxX, 1e-9); // Right to left: the first word on the right
        Assert.True(right.Frames[1].MaxX < right.Frames[0].MinX);
        var size = new SizeD(200, 100);
        var box = new StageRect(0, 0, 200, 100);
        Assert.Equal(box, TorqueGeometry.Turn(0, size, null).Apply(box));
        var turned = TorqueGeometry.Turn(60, size, null).Apply(box);
        Assert.True(turned.Width < 110);
        Assert.Equal(100, turned.MidX, 0.5);
        var swung = TorqueGeometry.Turn(60, size, new SizeD(1000, 1000)).Apply(box);
        Assert.True(swung.MidX > 150, "A face turned to the right swings round the column");
    }
}
