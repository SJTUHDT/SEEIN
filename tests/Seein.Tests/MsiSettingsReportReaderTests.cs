using Seein.Service;

namespace Seein.Tests;

public sealed class MsiSettingsReportReaderTests
{
    [Fact] public void ShiftedReadIsRetriedBeforeItsContentsCanBeUsed()
    {
        var bad=new byte[290];bad[0]=0x36;var valid=new byte[290];valid[0]=0x50;
        var reads=0;var waits=0;
        var actual=MsiSettingsReportReader.Read(()=>++reads==1?bad:valid,()=>waits++,afterWrite:true);
        Assert.Same(valid,actual);Assert.Equal(2,reads);Assert.Equal(2,waits);
    }
    [Fact] public void MalformedReadsFailWithinABoundedRetryBudget()
    {
        var reads=0;var waits=0;
        Assert.Throws<IOException>(()=>MsiSettingsReportReader.Read(()=>{reads++;return [0x50];},()=>waits++,afterWrite:false));
        Assert.Equal(4,reads);Assert.Equal(3,waits);
    }
}
