using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace Unai.MpegTsRecover;

static class Program
{
	static DetectionJob _detectJob = new();
	static bool _exitRequested = false;
	static bool _writeTsAfter = false;

	static void Main(string[] args)
	{
		Console.CancelKeyPress += (s, e) =>
		{
			if (!_exitRequested)
			{
				AnsiConsole.MarkupLine($"[red]Stop signal received[/]! Closing…");
				e.Cancel = !_exitRequested;
				_exitRequested = true;
			}
		};

		ParseCommandLineArguments(args);

		var mainJob = Task.Run(_detectJob.Start).ContinueWith(t =>
		{
			_exitRequested = true;
			if (t.IsFaulted)
			{
				AnsiConsole.WriteException(t.Exception);
			}
		});

		AnsiConsole.Progress()
			.Columns(new PercentageColumn(), new ProgressBarColumn(), new TaskDescriptionColumn(), new ElapsedTimeColumn())
			.Start(ctx =>
			{
				var mainTask = ctx.AddTask(_detectJob.JobDescription);

				while (!ctx.IsFinished && !_exitRequested)
				{
					lock (_detectJob.SyncLock)
					{
						mainTask.Description = _detectJob.JobDescription;
						mainTask.Value(100 * _detectJob.JobProgress);
					}
					Thread.Sleep(250);
				}
			});

		var table = new Table
		{
			Caption = new(_detectJob.InputFile)
		};
		table.AddColumns("Offset", "Size", "Format", "Timestamp");
		foreach (var range in _detectJob.DataRanges.OrderByDescending(r => r.Length).Take(25))
		{
			table.AddRow(
				$"0x{range.StartOffset:x12}/{range.StartOffset / 1048576} MiB",
				$"{range.Length / 1048576} MiB",
				$"{(range.IsM2TS ? "M2TS" : "MPEG-TS")}",
				range.FirstDateTime.HasValue ? range.FirstDateTime.Value.ToString("s") : "N/A"
			);
		}

		AnsiConsole.Write(table);
		AnsiConsole.WriteLine();

		if (_writeTsAfter)
		{
			foreach (var range in _detectJob.DataRanges.Where(r => r.Length >= 1024 * 512))
			{
				AnsiConsole.MarkupLine($"Writing TS output for range in {Utils.FormatOffset(range.StartOffset)}…");

				string outputPath = $"{Path.GetFileNameWithoutExtension(_detectJob.InputFile)}.0x{range.StartOffset:x12}";
				if (range.FirstDateTime.HasValue)
				{
					outputPath += $".{range.FirstDateTime.Value:yyyyMMdd_HHmmss}";
				}
				outputPath += ".ts";
				using var outputStr = File.OpenWrite(outputPath);
				using var outputStrBw = new BinaryWriter(outputStr);

				_detectJob._inputStr.Position = range.StartOffset;
				while (_detectJob._inputStr.Position < range.EndOffset)
				{
					outputStrBw.Write(_detectJob._br.ReadBytes(188));
					if (range.IsM2TS) _detectJob._inputStr.Position += 4;
				}
			}
		}
	}

	private static void ParseCommandLineArguments(string[] args)
	{
		foreach (string arg in args)
		{
			if (!arg.Contains('='))
			{
				_detectJob.InputFile = arg;
				continue;
			}
			else
			{
				var argKvp = arg.Split('=', 2);
				switch (argKvp[0])
				{
					case "--offset":
						_detectJob.StartOffset = long.Parse(argKvp[1]);
						break;

					case "--align":
						_detectJob.SkipAlignment = long.Parse(argKvp[1]);
						break;

					case "--write-on-detect":
						_detectJob.WriteFiltered = bool.Parse(argKvp[1]);
						break;

					case "--write":
						_writeTsAfter = bool.Parse(argKvp[1]);
						break;
				}
			}
		}
	}
}
