using System;

namespace Unai.MpegTsRecover;

public class MpegTsDataRange
{
	public long StartOffset { get; set; } = 0;
	public long EndOffset { get; set; } = 0;
	public bool IsM2TS { get; set; } = false;
	public DateTime? FirstDateTime { get; set; } = null;

	public long Length => EndOffset - StartOffset;
}
