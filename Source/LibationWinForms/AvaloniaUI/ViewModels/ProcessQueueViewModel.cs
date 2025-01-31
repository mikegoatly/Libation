﻿using ApplicationServices;
using Avalonia.Controls;
using Avalonia.Threading;
using DataLayer;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace LibationWinForms.AvaloniaUI.ViewModels
{
	public class ProcessQueueViewModel : ViewModelBase, ProcessQueue.ILogForm
	{
		public ObservableCollection<LogEntry> LogEntries { get; } = new();
		public TrackedQueue<ProcessBookViewModel> Items { get; } = new();

		private TrackedQueue<ProcessBookViewModel> Queue => Items;
		public ProcessBookViewModel SelectedItem { get; set; }
		public Task QueueRunner { get; private set; }
		public bool Running => !QueueRunner?.IsCompleted ?? false;

		private readonly ProcessQueue.LogMe Logger;

		public ProcessQueueViewModel()
		{
			Queue.QueuededCountChanged += Queue_QueuededCountChanged;
			Queue.CompletedCountChanged += Queue_CompletedCountChanged;
			Logger = ProcessQueue.LogMe.RegisterForm(this);
		}

		private int _completedCount;
		private int _errorCount;
		private int _queuedCount;
		private string _runningTime;
		private bool _progressBarVisible;

		public int CompletedCount { get => _completedCount; private set { this.RaiseAndSetIfChanged(ref _completedCount, value); this.RaisePropertyChanged(nameof(AnyCompleted)); } }
		public int QueuedCount { get => _queuedCount; private set { this.RaiseAndSetIfChanged(ref _queuedCount, value); this.RaisePropertyChanged(nameof(AnyQueued)); } }
		public int ErrorCount { get => _errorCount; private set { this.RaiseAndSetIfChanged(ref _errorCount, value); this.RaisePropertyChanged(nameof(AnyErrors)); } }
		public string RunningTime { get => _runningTime; set { this.RaiseAndSetIfChanged(ref _runningTime, value); } }
		public bool ProgressBarVisible { get => _progressBarVisible; set { this.RaiseAndSetIfChanged(ref _progressBarVisible, value); } }
		public bool AnyCompleted => CompletedCount > 0;
		public bool AnyQueued => QueuedCount > 0;
		public bool AnyErrors => ErrorCount > 0;
		public double Progress => 100d * Queue.Completed.Count / Queue.Count;

		private void Queue_CompletedCountChanged(object sender, int e)
		{
			int errCount = Queue.Completed.Count(p => p.Result is ProcessBookResult.FailedAbort or ProcessBookResult.FailedSkip or ProcessBookResult.FailedRetry or ProcessBookResult.ValidationFail);
			int completeCount = Queue.Completed.Count(p => p.Result is ProcessBookResult.Success);

			ErrorCount = errCount;
			CompletedCount = completeCount;
			this.RaisePropertyChanged(nameof(Progress));
		}
		private void Queue_QueuededCountChanged(object sender, int cueCount)
		{
			QueuedCount = cueCount;
			this.RaisePropertyChanged(nameof(Progress));
		}

		public void WriteLine(string text)
		{
			Dispatcher.UIThread.Post(() =>
				LogEntries.Add(new()
				{
					LogDate = DateTime.Now,
					LogMessage = text.Trim()
				}));
		}


		#region Add Books to Queue

		private bool isBookInQueue(LibraryBook libraryBook)
			=> Queue.Any(b => b?.LibraryBook?.Book?.AudibleProductId == libraryBook.Book.AudibleProductId);

		public void AddDownloadPdf(LibraryBook libraryBook)
			=> AddDownloadPdf(new List<LibraryBook>() { libraryBook });

		public void AddDownloadDecrypt(LibraryBook libraryBook)
			=> AddDownloadDecrypt(new List<LibraryBook>() { libraryBook });

		public void AddConvertMp3(LibraryBook libraryBook)
			=> AddConvertMp3(new List<LibraryBook>() { libraryBook });

		public void AddDownloadPdf(IEnumerable<LibraryBook> entries)
		{
			List<ProcessBookViewModel> procs = new();
			foreach (var entry in entries)
			{
				if (isBookInQueue(entry))
					continue;

				ProcessBookViewModel pbook = new(entry, Logger);
				pbook.AddDownloadPdf();
				procs.Add(pbook);
			}

			Serilog.Log.Logger.Information("Queueing {count} books", procs.Count);
			AddToQueue(procs);
		}

		public void AddDownloadDecrypt(IEnumerable<LibraryBook> entries)
		{
			List<ProcessBookViewModel> procs = new();
			foreach (var entry in entries)
			{
				if (isBookInQueue(entry))
					continue;

				ProcessBookViewModel pbook = new(entry, Logger);
				pbook.AddDownloadDecryptBook();
				pbook.AddDownloadPdf();
				procs.Add(pbook);
			}

			Serilog.Log.Logger.Information("Queueing {count} books", procs.Count);
			AddToQueue(procs);
		}

		public void AddConvertMp3(IEnumerable<LibraryBook> entries)
		{
			List<ProcessBookViewModel> procs = new();
			foreach (var entry in entries)
			{
				if (isBookInQueue(entry))
					continue;

				ProcessBookViewModel pbook = new(entry, Logger);
				pbook.AddConvertToMp3();
				procs.Add(pbook);
			}

			Serilog.Log.Logger.Information("Queueing {count} books", procs.Count);
			AddToQueue(procs);
		}

		public void AddToQueue(IEnumerable<ProcessBookViewModel> pbook)
		{
			Dispatcher.UIThread.Post(() =>
			{
				Queue.Enqueue(pbook);
				if (!Running)
					QueueRunner = QueueLoop();
			});
		}

		#endregion

		DateTime StartingTime;
		private async Task QueueLoop()
		{
			try
			{
				Serilog.Log.Logger.Information("Begin processing queue");

				RunningTime = string.Empty;
				ProgressBarVisible = true;
				StartingTime = DateTime.Now;

				using var counterTimer = new System.Threading.Timer(CounterTimer_Tick, null, 0, 500);

				while (Queue.MoveNext())
				{
					var nextBook = Queue.Current;

					Serilog.Log.Logger.Information("Begin processing queued item. {item_LibraryBook}", nextBook?.LibraryBook);

					var result = await nextBook.ProcessOneAsync();

					Serilog.Log.Logger.Information("Completed processing queued item: {item_LibraryBook}\r\nResult: {result}", nextBook?.LibraryBook, result);

					if (result == ProcessBookResult.ValidationFail)
						Queue.ClearCurrent();
					else if (result == ProcessBookResult.FailedAbort)
						Queue.ClearQueue();
					else if (result == ProcessBookResult.FailedSkip)
						nextBook.LibraryBook.Book.UpdateBookStatus(DataLayer.LiberatedStatus.Error);
				}
				Serilog.Log.Logger.Information("Completed processing queue");

				Queue_CompletedCountChanged(this, 0);
				ProgressBarVisible = false;
			}
			catch (Exception ex)
			{
				Serilog.Log.Logger.Error(ex, "An error was encountered while processing queued items");
			}
		}

		private void CounterTimer_Tick(object? state)
		{
			string timeToStr(TimeSpan time)
			{
				string minsSecs = $"{time:mm\\:ss}";
				if (time.TotalHours >= 1)
					return $"{time.TotalHours:F0}:{minsSecs}";
				return minsSecs;
			}
			RunningTime = timeToStr(DateTime.Now - StartingTime);
		}
	}

	public class LogEntry
	{
		public DateTime LogDate { get; init; }
		public string LogDateString => LogDate.ToShortTimeString();
		public string LogMessage { get; init; }
	}
}
