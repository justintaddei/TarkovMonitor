﻿using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace TarkovMonitor
{
    internal class GameWatcher
    {
        private Process? process;
        private readonly System.Timers.Timer processTimer;
        private readonly FileSystemWatcher watcher;
        //private event EventHandler<NewLogEventArgs> NewLog;
        private readonly Dictionary<GameLogType, LogMonitor> monitors;
        private RaidInfo raidInfo;
        public event EventHandler<NewLogDataEventArgs> NewLogData;
        public event EventHandler<ExceptionEventArgs> ExceptionThrown;
        public event EventHandler<DebugEventArgs> DebugMessage;
        public event EventHandler GameStarted;
        public event EventHandler<GroupInviteEventArgs> GroupMatchInvite;
        public event EventHandler<GroupReadyEventArgs> GroupReady;
        public event EventHandler GroupDisbanded;
        public event EventHandler<GroupUserLeaveEventArgs> GroupUserLeave;
        public event EventHandler<MatchingStartedEventArgs> MatchingStarted;
        public event EventHandler<MatchFoundEventArgs> MatchFound;
        public event EventHandler<MatchingCancelledEventArgs> MatchingAborted;
        public event EventHandler<RaidLoadedEventArgs> RaidLoaded;
        public event EventHandler<RaidExitedEventArgs> RaidExited;
        public event EventHandler<TaskModifiedEventArgs> TaskModified;
        public event EventHandler<TaskEventArgs> TaskStarted;
        public event EventHandler<TaskEventArgs> TaskFailed;
        public event EventHandler<TaskEventArgs> TaskFinished;
        public event EventHandler<FleaSoldEventArgs> FleaSold;
        public event EventHandler<FleaOfferExpiredEventArgs> FleaOfferExpired;
        public GameWatcher()
        {
            monitors = new();
            raidInfo = new RaidInfo();
            processTimer = new System.Timers.Timer(TimeSpan.FromSeconds(30).TotalMilliseconds)
            {
                AutoReset = true,
                Enabled = false
            };
            processTimer.Elapsed += ProcessTimer_Elapsed;
            watcher = new FileSystemWatcher { 
                Filter = "*.log",
                IncludeSubdirectories = true,
                EnableRaisingEvents = false,
            };
            watcher.Created += Watcher_Created;
            UpdateProcess();
        }

        public void Start()
        {
            UpdateProcess();
            processTimer.Enabled = true;
        }

        private void Watcher_Created(object sender, FileSystemEventArgs e)
        {
            if (e.Name.Contains("application.log"))
            {
                StartNewMonitor(e.FullPath);
            }
            if (e.Name.Contains("notifications.log"))
            {
                StartNewMonitor(e.FullPath);
            }
        }

        private void GameWatcher_NewLogData(object? sender, NewLogDataEventArgs e)
        {
            try
            {
                //DebugMessage?.Invoke(this, new DebugEventArgs(e.NewMessage));
                NewLogData?.Invoke(this, e);
                var logPattern = @"(?<message>^\d{4}-\d{2}-\d{2}.+$)\s*(?<json>^{[\s\S]+?^})*";
                var logMessages = Regex.Matches(e.Data, logPattern, RegexOptions.Multiline);
                /*Debug.WriteLine("===log chunk start===");
                Debug.WriteLine(e.NewMessage);
                Debug.WriteLine("===log chunk end===");*/
                foreach (Match logMessage in logMessages)
                {
                    var eventLine = logMessage.Groups["message"].Value;
                    var jsonString = "{}";
                    if (logMessage.Groups["json"].Success)
                    {
                        jsonString = logMessage.Groups["json"].Value;
                    }
                    /*Debug.WriteLine("logged message");
                    Debug.WriteLine(eventLine);
                    Debug.WriteLine("logged json");
                    Debug.WriteLine(jsonString);*/
                    var jsonNode = JsonNode.Parse(jsonString);
                    if (eventLine.Contains("Got notification | UserMatchOver"))
                    {
                        RaidExited?.Invoke(this, new RaidExitedEventArgs { Map = jsonNode["location"].ToString(), RaidId = jsonNode["shortId"]?.ToString() });
                    }
                    if (eventLine.Contains("Got notification | GroupMatchInviteAccept") || eventLine.Contains("Got notification | GroupMatchInviteSend"))
                    {
                        // GroupMatchInviteAccept occurs when someone you send an invite accepts
                        // GroupMatchInviteSend occurs when you receive an invite and either accept or decline
                        GroupMatchInvite?.Invoke(this, new(jsonNode));
                    }
                    if (eventLine.Contains("Got notification | GroupMatchUserLeave"))
                    {
                        // User left the group
                        GroupUserLeave?.Invoke(this, new(jsonNode));
                    }
					if (eventLine.Contains("Got notification | GroupMatchWasRemoved"))
                    {
                        // When the group is disbanded
                        GroupDisbanded?.Invoke(this, new());
                    }
					if (eventLine.Contains("Got notification | GroupMatchRaidReady"))
                    {
                        // Occurs for each other member of the group when ready
                        GroupReady?.Invoke(this, new GroupReadyEventArgs(jsonNode));
                    }
                    if (eventLine.Contains("application|LocationLoaded"))
                    {
						// The map has been loaded and the game is searching for a match
						raidInfo = new()
						{
							MapLoadTime = float.Parse(Regex.Match(eventLine, @"LocationLoaded:[0-9.]+ real:(?<loadTime>[0-9.]+)").Groups["loadTime"].Value)
						};
						MatchingStarted?.Invoke(this, new(raidInfo));
					}
					if (eventLine.Contains("application|MatchingCompleted"))
					{
						// Matching is complete and we are locked to a server with other players
						// Just the queue time is available so far
						// Occurs on initial raid load and when the user cancels matching
                        // Does not occur when the user re-connects to a raid in progress
						var queueTimeMatch = Regex.Match(eventLine, @"MatchingCompleted:[0-9.]+ real:(?<queueTime>[0-9.]+)");
						raidInfo.QueueTime = float.Parse(queueTimeMatch.Groups["queueTime"].Value);
					}
                    if (eventLine.Contains("application|TRACE-NetworkGameCreate profileStatus"))
                    {
                        // Immediately after matching is complete
                        // Sufficient information is available to raise the MatchFound event
                        raidInfo.Map = Regex.Match(eventLine, "Location: (?<map>[^,]+)").Groups["map"].Value;
                        raidInfo.Online = eventLine.Contains("RaidMode: Online");
                        raidInfo.RaidId = Regex.Match(eventLine, @"shortId: (?<raidId>[A-Z0-9]{6})").Groups["raidId"].Value;
                        if (raidInfo.Online && raidInfo.QueueTime > 0)
                        {
                            // Raise the MatchFound event only if we queued; not if we are re-loading back into a raid
                            MatchFound?.Invoke(this, new(raidInfo));
                        }
                    }
                    if (eventLine.Contains("application|GameStarting"))
                    {
                        // The raid start countdown begins. Only happens for PMCs.
                        raidInfo.RaidType = RaidType.PMC;
                        if (raidInfo.Online)
                        {
                            RaidLoaded?.Invoke(this, new(raidInfo));
                        }
                    }
                    if (eventLine.Contains("application|GameStarted"))
                    {
                        // Raid begins, either at the end of the countdown for PMC, or immediately as a scav
                        if (raidInfo.RaidType == RaidType.Unknown && raidInfo.QueueTime > 0)
                        {
                            // RaidType was not set previously for PMC, and we spent time matching, so we must be a scav
                            raidInfo.RaidType = RaidType.Scav;
                        }
                        if (raidInfo.Online && raidInfo.RaidType != RaidType.PMC)
                        {
                            // We already raised the RaidLoaded event for PMC, so only raise here if not PMC
                            RaidLoaded?.Invoke(this, new(raidInfo));
                        }
                        raidInfo = new();
                    }
                    if (eventLine.Contains("application|Network game matching aborted") || eventLine.Contains("application|Network game matching cancelled"))
                    {
                        // User cancelled matching
                        MatchingAborted?.Invoke(this, new(raidInfo));
                        raidInfo = new();
                    }
                    if (eventLine.Contains("Got notification | ChatMessageReceived"))
                    {
                        var messageText = jsonNode["message"]["text"].ToString();
                        var messageType = jsonNode["message"]["type"].GetValue<int>();

                        if (messageType == 4)
						{
							var templateId = jsonNode["message"]["templateId"].ToString();
							if (templateId == "5bdabfb886f7743e152e867e 0")
							{
								FleaSold?.Invoke(this, new FleaSoldEventArgs(jsonNode));
								continue;
							}
							if (templateId == "5bdabfe486f7743e1665df6e 0")
							{
								FleaOfferExpired?.Invoke(this, new FleaOfferExpiredEventArgs(jsonNode));
								continue;
							}
						}
                        if (Enum.IsDefined(typeof(TaskStatus), messageType))
                        {
                            var args = new TaskModifiedEventArgs(jsonNode);
                            TaskModified?.Invoke(this, args);
                            if (args.Status == TaskStatus.Started)
                            {
                                TaskStarted?.Invoke(this, new TaskEventArgs { TaskId = args.TaskId });
                            }
                            if (args.Status == TaskStatus.Failed)
                            {
                                TaskFailed?.Invoke(this, new TaskEventArgs { TaskId = args.TaskId });
                            }
                            if (args.Status == TaskStatus.Finished)
                            {
                                TaskFinished?.Invoke(this, new TaskEventArgs { TaskId = args.TaskId });
                            }
                        }
                    }
                    
                }
            }
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex));
            }
        }

        private void ProcessTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            UpdateProcess();
        }

        private void UpdateProcess()
        {
            if (process != null)
            {
                if (!process.HasExited)
                {
                    return;
                }
                //DebugMessage?.Invoke(this, new DebugEventArgs("EFT exited."));
                process = null;
            }
            raidInfo = new();
            var processes = Process.GetProcessesByName("EscapeFromTarkov");
            if (processes.Length == 0) {
                //DebugMessage?.Invoke(this, new DebugEventArgs("EFT not running."));
                process = null;
                return;
            }
            GameStarted?.Invoke(this, new EventArgs());
            process = processes.First();
            var exePath = GetProcessFilename.GetFilename(process);
            var path = exePath[..exePath.LastIndexOf(Path.DirectorySeparatorChar)];
            var logsPath = System.IO.Path.Combine(path, "Logs");
            watcher.Path = logsPath;
            watcher.EnableRaisingEvents = true;
            var logFolders = System.IO.Directory.GetDirectories(logsPath);
            var latestDate = new DateTime(0);
            var latestLogFolder = logFolders.Last();
            foreach (var logFolder in logFolders)
            {
                var dateTimeString = Regex.Match(logFolder, @"log_(?<timestamp>\d+\.\d+\.\d+_\d+-\d+-\d+)").Groups["timestamp"].Value;
                var logDate = DateTime.ParseExact(dateTimeString, "yyyy.MM.dd_H-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
                if (logDate > latestDate)
                {
                    latestDate = logDate;
                    latestLogFolder = logFolder;
                }
            }
            var files = System.IO.Directory.GetFiles(latestLogFolder);
            foreach (var file in files)
            {
                if (file.Contains("notifications.log"))
                {
                    StartNewMonitor(file);
                }
                if (file.Contains("application.log"))
                {
                    StartNewMonitor(file);
                }
                /*if (file.Contains("traces.log"))
                {
                    StartNewMonitor(file);
                }*/
            }
        }

        private void StartNewMonitor(string path)
        {
            GameLogType? newType = null;
            if (path.Contains("application.log"))
            {
                newType = GameLogType.Application;
            }
            if (path.Contains("notifications.log"))
            {
                newType = GameLogType.Notifications;
            }
            if (path.Contains("traces.log"))
            {
                newType = GameLogType.Traces;
            }
            if (newType != null)
            {
                //Debug.WriteLine($"Starting new {newType} monitor at {path}");
                if (monitors.ContainsKey((GameLogType)newType))
                {
                    monitors[(GameLogType)newType].Stop();
                }
                var newMon = new LogMonitor(path, (GameLogType)newType);
                newMon.NewLogData += GameWatcher_NewLogData;
                newMon.Start();
                monitors[(GameLogType)newType] = newMon;
            }
        }
	}
	public enum GameLogType
	{
		Application,
		Notifications,
		Traces
	}
    public enum MessageType
	{
		PlayerMessage = 1,
		Started = 10,
		Failed = 11,
		Finished = 12
	}
	public enum TaskStatus
	{
		Started = 10,
		Failed = 11,
		Finished = 12
	}
	public enum RaidType
	{
		Unknown,
		PMC,
		Scav
	}
	public enum GroupInviteType
	{
		Accepted,
		Sent
    }
    public class RaidInfo
    {
        public string Map { get; set; }
        public string RaidId { get; set; }
        public bool Online { get; set; }
        public float MapLoadTime { get; set; }
        public float QueueTime { get; set; }
        public RaidType RaidType { get; set; }
        public RaidInfo()
        {
            Map = "";
            Online = false;
            RaidId = "";
            MapLoadTime = 0;
            QueueTime = 0;
            RaidType = RaidType.Unknown;
        }
    }
    public class RaidExitedEventArgs : EventArgs
	{
		public string Map { get; set; }
		public string RaidId { get; set; }
	}
	public class TaskModifiedEventArgs : EventArgs
	{
		public string TaskId { get; set; }
		public TaskStatus Status { get; set; }
		public TaskModifiedEventArgs(JsonNode node)
		{
			TaskId = node["message"]["templateId"].ToString().Split(' ')[0];
			Status = (TaskStatus)node["message"]["type"].GetValue<int>();
		}
	}
	public class TaskEventArgs : EventArgs
	{
		public string TaskId { get; set; }
	}
    public class GroupInviteEventArgs : EventArgs
    {
        public PlayerInfo PlayerInfo { get; set; }
        public GroupInviteType InviteType { get; set; }
        public GroupInviteEventArgs(JsonNode node)
        {
            if (node["type"].ToString() == "groupMatchInviteAccept")
            {
                InviteType = GroupInviteType.Accepted;
            } else
            {
                InviteType = GroupInviteType.Sent;
            }
            PlayerInfo = new PlayerInfo(node["Info"]);
        }
    }
    public class GroupUserLeaveEventArgs : EventArgs
    {
        public string Nickname { get; set; }
        public GroupUserLeaveEventArgs(JsonNode node)
        {
            Nickname = node["Nickname"].ToString();
        }
    }
	public class GroupReadyEventArgs : EventArgs
	{
		public PlayerInfo PlayerInfo { get; set; }
		public PlayerLoadout PlayerLoadout { get; set; }
		public GroupReadyEventArgs(JsonNode node)
		{
			this.PlayerInfo = new PlayerInfo(node["extendedProfile"]["Info"]);
			this.PlayerLoadout = new PlayerLoadout(node["extendedProfile"]["PlayerVisualRepresentation"]);
		}
		public override string ToString()
		{
			return $"{this.PlayerInfo.Nickname} ({this.PlayerLoadout.Info.Side}, {this.PlayerLoadout.Info.Level})";
		}
	}
    public class MatchingStartedEventArgs : EventArgs
    {
        public float MapLoadTime { get; set; }
        public MatchingStartedEventArgs(RaidInfo raidInfo)
        {
            MapLoadTime = raidInfo.MapLoadTime;
        }
    }
    public class MatchingCancelledEventArgs : MatchingStartedEventArgs
    {
        public float QueueTime { get; set; }
        public MatchingCancelledEventArgs(RaidInfo raidInfo) : base(raidInfo)
        {
            QueueTime = raidInfo.QueueTime;
        }
    }
	public class MatchFoundEventArgs : MatchingStartedEventArgs
    {
		public string Map { get; set; }
		public string RaidId { get; set; }
		public float QueueTime { get; set; }
        public MatchFoundEventArgs(RaidInfo raidInfo) : base(raidInfo)
        {
            Map = raidInfo.Map;
            RaidId = raidInfo.RaidId;
            QueueTime = raidInfo.QueueTime;
        }
    }
	public class RaidLoadedEventArgs : MatchFoundEventArgs
    {
		public RaidType RaidType { get; set; }
        public RaidLoadedEventArgs(RaidInfo raidInfo) : base(raidInfo)
        {
            RaidType = raidInfo.RaidType;
        }
	}
	public class FleaSoldEventArgs : EventArgs
	{
		public string Buyer { get; set; }
		public string SoldItemId { get; set; }
		public int SoldItemCount { get; set; }
		public Dictionary<string, int> ReceivedItems { get; set; }
		public FleaSoldEventArgs(JsonNode node)
		{
			Buyer = node["message"]["systemData"]["buyerNickname"].ToString();
			SoldItemId = node["message"]["systemData"]["soldItem"].ToString();
			SoldItemCount = node["message"]["systemData"]["itemCount"].GetValue<int>();
			ReceivedItems = new Dictionary<string, int>();
			if (node["message"]["hasRewards"] != null && node["message"]["hasRewards"].GetValue<bool>())
			{
				foreach (var item in node["message"]["items"]["data"].AsArray())
				{
					ReceivedItems.Add(item["_tpl"].ToString(), item["upd"]["StackObjectsCount"].GetValue<int>());
				}
			}
		}
	}
	public class FleaOfferExpiredEventArgs : EventArgs
	{
		public string ItemId { get; set; }
		public int ItemCount { get; set; }
		public FleaOfferExpiredEventArgs(JsonNode node)
		{
			var item = node["message"]["items"]["data"].AsArray()[0];
			ItemId = item["_tpl"].ToString();
			ItemCount = item["upd"]["StackObjectsCount"].GetValue<int>();
		}
	}
	public class ExceptionEventArgs : EventArgs
	{
		public Exception Exception { get; set; }
		public ExceptionEventArgs(Exception ex)
		{
			this.Exception = ex;
		}
	}
	public class DebugEventArgs : EventArgs
	{
		public string Message { get; set; }
		public DebugEventArgs(string message)
		{
			this.Message = message;
		}
	}
}
