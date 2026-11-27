namespace Seein.Service;

internal static class MsiSettingsReportReader
{
    internal static byte[] Read(Func<byte[]> read, Action settle, bool afterWrite)
    {
        if(afterWrite)settle();
        for(int attempt=0;attempt<4;attempt++)
        {
            var report=read();
            if(report.Length==290&&report[0]==0x50)return report;
            if(attempt<3)settle();
        }
        throw new IOException("MSI 主板设置报文头或长度无效，已停止写入并等待重新连接");
    }
}
