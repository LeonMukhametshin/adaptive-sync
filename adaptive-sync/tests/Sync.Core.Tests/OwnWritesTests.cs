using Sync.Core.Strategies;

namespace Sync.Core.Tests;

public class OwnWritesTests
{
    [Fact]
    public void Mask_HidesOwnWritesOfUncontestedFieldsAndKeepsForeignChanges()
    {
        var own = new OwnWrites();
        own.Mask(new ServerRecord { RowVersion = 7, FieldVersions = { ["level"] = new FieldVersion(7, 30) } }, baseRowVersion: 4);
        own.Add(9);
        var current = new ServerRecord
        {
            RowVersion = 9,
            FieldVersions =
            {
                ["level"] = new FieldVersion(7, 30),
                ["coins"] = new FieldVersion(9, 40),
                ["nickname"] = new FieldVersion(2, 5),
            },
        };

        var masked = own.Mask(current, baseRowVersion: 4);

        Assert.Equal(7, masked.RowVersion);
        Assert.Equal(7, masked.FieldVersions["level"].RowVersion);
        Assert.Equal(4, masked.FieldVersions["coins"].RowVersion);
        Assert.Equal(40, masked.FieldVersions["coins"].LogicalTime);
        Assert.Equal(2, masked.FieldVersions["nickname"].RowVersion);
        Assert.Equal(9, current.FieldVersions["coins"].RowVersion);
    }

    [Fact]
    public void Mask_FieldContestedBeforeOwnWrite_StaysConflicting()
    {
        var own = new OwnWrites();
        own.Mask(new ServerRecord { RowVersion = 6, FieldVersions = { ["coins"] = new FieldVersion(6, 20) } }, baseRowVersion: 4);
        own.Add(9);

        var masked = own.Mask(new ServerRecord { RowVersion = 9, FieldVersions = { ["coins"] = new FieldVersion(9, 40) } }, baseRowVersion: 4);

        Assert.Equal(9, masked.FieldVersions["coins"].RowVersion);
        Assert.Equal(9, masked.RowVersion);
    }

    [Fact]
    public void Mask_RecordWrittenOnlyByOwnOperations_LooksSeen()
    {
        var own = new OwnWrites();
        own.Add(11);
        own.Add(12);

        var masked = own.Mask(new ServerRecord { RowVersion = 12, FieldVersions = { ["quantity"] = new FieldVersion(12, 3) } }, baseRowVersion: 0);

        Assert.Equal(0, masked.RowVersion);
        Assert.Equal(0, masked.FieldVersions["quantity"].RowVersion);
    }

    [Fact]
    public void Mask_WithoutOwnWrites_ReturnsSameRecord()
    {
        var current = new ServerRecord { RowVersion = 3 };

        Assert.Same(current, new OwnWrites().Mask(current, 1));
    }
}
