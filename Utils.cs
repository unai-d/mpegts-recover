namespace Unai.MpegTsRecover;

public static class Utils
{
	public static int FromBcd(byte bcd)
	{
		return (bcd & 0xf) + ((bcd & 0xf0) >> 4) * 10;
	}

	public static string FormatOffset(long ofs)
	{
		return $"[blue]0x{ofs:x12}[/]/[yellow]{ofs/1048576} MiB[/]";
	}
}
