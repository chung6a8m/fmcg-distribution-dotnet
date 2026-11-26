using FmcgDms.Api.Persistence;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class App05RemainderClosureTests
{
    [Theory]
    [InlineData("1.001", "Điểm giao yêu cầu dừng", true)]
    [InlineData("0", "Điểm giao yêu cầu dừng", false)]
    [InlineData("0.0001", "Điểm giao yêu cầu dừng", false)]
    [InlineData("1", " ", false)]
    [InlineData("1", "Lý do\nkhông hợp lệ", false)]
    public void Proposal_binds_a_positive_base_quantity_and_a_reason(string quantity, string reason, bool valid)
    {
        var input = new App05CloseReadiness.ClosureProposalInput(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture), reason, new string('A', 64));
        Assert.Equal(valid, App05CloseReadiness.IsClosureProposalInputValid(input));
        Assert.False(App05CloseReadiness.IsClosureProposalInputValid(input with { ExpectedRevision = "old" }));
        Assert.False(App05CloseReadiness.IsClosureProposalInputValid(input with { ConfirmSourceId = Guid.Empty }));
    }
}
