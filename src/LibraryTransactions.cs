/***************************************************************************
 *  LibraryTransactions.cs
 *
 *  Copyright (C) 2005 Novell
 *  Written by Aaron Bockover (aaron@aaronbock.net)
 ****************************************************************************/

/*  THIS FILE IS LICENSED UNDER THE MIT LICENSE AS OUTLINED IMMEDIATELY BELOW: 
 *
 *  Permission is hereby granted, free of charge, to any person obtaining a
 *  copy of this software and associated documentation files (the "Software"),  
 *  to deal in the Software without restriction, including without limitation  
 *  the rights to use, copy, modify, merge, publish, distribute, sublicense,  
 *  and/or sell copies of the Software, and to permit persons to whom the  
 *  Software is furnished to do so, subject to the following conditions:
 *
 *  The above copyright notice and this permission notice shall be included in 
 *  all copies or substantial portions of the Software.
 *
 *  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
 *  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
 *  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
 *  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
 *  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
 *  FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
 *  DEALINGS IN THE SOFTWARE.
 */
 
using System;
using System.Collections;
using System.Threading;
using System.IO;
using System.Data;
using System.Text.RegularExpressions;
using Sql; 

namespace Sonance 
{
	// --- Base LibraryTransaction Class 
	
	public abstract class LibraryTransaction
	{
		public event EventHandler Finished;
		public Thread ExecutingThread;
		
		// statistics fields for any UI progress display
		protected long totalCount;
		protected long currentCount;
		protected string statusMessage;
		
		protected long averageDuration;
		
		protected bool cancelRequested;
		
		protected bool showStatus;
		
		public abstract string Name {
			get;
		}

		public abstract void Run();
		
		public LibraryTransaction()
		{
			Finished = null;
			ExecutingThread = null;
			showStatus = true;
		}
		
		public bool ThreadedRun()
		{
			try {
				ExecutingThread = new Thread(SafeRun);
				ExecutingThread.Start();
			} catch(Exception) {
				return false;
			}
			
			return true;
		}
		
		public void SafeRun()
		{
			try {
				Run();
			} catch(Exception) {
				DebugLog.Add("LibraryTransaction threw an unhandled " + 
					"exception, ending transaction safely");
			}
			
			Finish(this);
		}
		
		public void Cancel()
		{
			cancelRequested = true;
			
			if(ExecutingThread == null)
				return;
			
			ExecutingThread.Join(new TimeSpan(0, 0, 1));
			
			if(ExecutingThread.IsAlive) {
				try {
					ExecutingThread.Abort();
				} catch(Exception) {}
				
				DebugLog.Add("Forcefully canceled LibraryTransaction");
				return;
			}
			
			DebugLog.Add("Peacefully canceled LibraryTransaction");
		}
		
		protected void UpdateAverageDuration(DateTime start)
		{
			long timeDiff = System.DateTime.Now.Ticks - start.Ticks;
			
			if(timeDiff > 0) {
				if(averageDuration == 0)
					averageDuration = timeDiff;
				else
					averageDuration = (averageDuration + timeDiff) / 2;
			}
		}
		
		public void Finish(object o)
		{
			EventHandler handler = Finished;
			if(handler != null) 
				handler(o, new EventArgs());
		}
		
		public long TotalCount {
			get {
				return totalCount;
			}
		}
		
		public long CurrentCount {
			get {
				return currentCount;
			}
		}
		
		public string StatusMessage {
			get {
				return statusMessage;
			}
		}
		
		public long AverageDuration {
			get {
				return averageDuration;
			}
		}
		
		public bool ShowStatus {
			get {
				return showStatus;
			}
		}
	}
	
	// --- Implementing LibraryTransaction Classes
	
	public class HaveTrackInfoArgs : EventArgs
	{
		public TrackInfo TrackInfo;
	}

	public delegate void HaveTrackInfoHandler(object o, HaveTrackInfoArgs args);
	
	public class FileLoadTransaction : LibraryTransaction
	{
		private string path;
		private bool allowLibrary, preload;
		
		public event HaveTrackInfoHandler HaveTrackInfo;
		
		public override string Name {
			get {
				return "Library Track Loader";
			}
		}
		
		public FileLoadTransaction(string path) :
			this(path, true, true) {}
		
		public FileLoadTransaction(string path, bool allowLibrary) :
			this(path, allowLibrary, true) {}
		
		public FileLoadTransaction(string path, bool allowLibrary, 
			bool preload)
		{
			this.path = path;
			this.allowLibrary = allowLibrary;
			this.preload = preload;
		}
		
		public override void Run()
		{
			totalCount = 0;
			currentCount = 0;
			statusMessage = "Processing";
			
			AddMultipleFilesRaw(path);
		}
		
		private void AddMultipleFilesRaw(string rawData)
		{
			if(rawData == null)
				return;
			
			foreach(string file in rawData.Split('\n')) {
				if(file == null)
					continue;
					
				string uri = StringUtil.UriToFileName(file.Trim()).Trim();
				if(uri.Length == 0)
					continue;
				
				DirectoryInfo di;
				
				try {
					di = new DirectoryInfo(uri);
				} catch(Exception) {
					continue;
				}
							
				if(preload) {
					statusMessage = "Preloading Files";
					totalCount = FileCount(di);
				}

				AddFile(uri);
				
				if(cancelRequested)
					return;
			}
		}
		
		private void AddFile(string uri)
		{
			if(RecurseDirectory(uri))
				return;
				
			if(cancelRequested)
				return;
				
			DateTime startStamp = DateTime.Now;
			TrackInfo ti = null;

			try {
				ti = new TrackInfo(uri/*, allowLibrary*/);
			} catch(Exception e) { 
				return;
			}
			
			RaiseTrackInfo(ti);
			UpdateAverageDuration(startStamp);
		}
		
		private void RaiseTrackInfo(TrackInfo ti)
		{
			statusMessage = "Loading " + ti.Artist + " - " + ti.Title;
			currentCount++;
			
			HaveTrackInfoHandler handler = HaveTrackInfo;
			if(handler != null) {
				HaveTrackInfoArgs args = new HaveTrackInfoArgs();
				args.TrackInfo = ti;
				handler(this, args);
			}
		}
		
		private bool RecurseDirectory(string path)
		{
			DirectoryInfo di;
			
			try {
				di = new DirectoryInfo(path);
			} catch(Exception) {
				return false;
			}
			
			if(!di.Exists)
				return false;
			
			foreach(DirectoryInfo sdi in di.GetDirectories()) {
				if(cancelRequested)
					return false;
					
				if(!sdi.Name.StartsWith(".")) 
					RecurseDirectory(path + "/" + sdi.Name);
			}
					
			foreach(FileInfo fi in di.GetFiles()) {
				if(cancelRequested)
					return false;
			
				AddFile(path + "/" + fi.Name);
			}
			
			return true;
		}
		
		private long FileCount(DirectoryInfo baseDirectory) 
	    {    
	        try { 
		        long count = baseDirectory.GetFiles().Length;

		        foreach(DirectoryInfo di in baseDirectory.GetDirectories()) {
		        	if(cancelRequested)
						return -1;
		            count += FileCount(di);
		         } 

		        return count;
		  	} catch(Exception) {
		  		return 0;
		  	}
	    }
	}
	
	public class PlaylistSaveTransaction : LibraryTransaction
	{	
		private Playlist pl;
	
		public override string Name 
		{
			get {
				return "Playlist Save";
			}
		}
		
		public PlaylistSaveTransaction(Playlist pl)
		{
			this.pl = pl;
		}
		
		public override void Run()
		{
			Statement query;
			int playlistId = Playlist.GetId(pl.name);
			
			statusMessage = "Flushing old entries";
			totalCount = pl.items.Count;
			currentCount = 0;

			if(playlistId == 0) {
				query = new Insert("Playlists", false, null, pl.name);
				Core.Library.Db.Execute(query);
				playlistId = Playlist.GetId(pl.name);
			}

			query = new Delete("PlaylistEntries") +
				new Where(new Compare("PlaylistID", Op.EqualTo, playlistId));
			Core.Library.Db.Execute(query);

			statusMessage = "Saving new entries";
			
			foreach(TrackInfo ti in pl.items) {
				if(cancelRequested)
					break;
			
				DateTime startStamp = DateTime.Now;
			
				if(ti.TrackId <= 0)
					continue;
					
				query = new Insert("PlaylistEntries", 
					false, null, playlistId, ti.TrackId);
					
				Core.Library.Db.Execute(query);
				
				UpdateAverageDuration(startStamp);
				currentCount++;
			}
		}
	}
	
	public class LibraryLoadTransaction : LibraryTransaction
	{
		public event HaveTrackInfoHandler HaveTrackInfo;
	
		public override string Name 
		{
			get {
				return "Library Load";
			}
		}
		
		public LibraryLoadTransaction()
		{
			showStatus = false;
		}
		
		public override void Run()
		{
			statusMessage = "Preloading Library";
			totalCount = Core.Library.Tracks.Count;
			currentCount = 0;
			
			foreach(TrackInfo track in Core.Library.Tracks.Values)
				RaiseTrackInfo(track);
		}
		
		private void RaiseTrackInfo(TrackInfo ti)
		{
			statusMessage = "Loading " + ti.Artist + " - " + ti.Title;
			currentCount++;
			
			HaveTrackInfoHandler handler = HaveTrackInfo;
			if(handler != null) {
				HaveTrackInfoArgs args = new HaveTrackInfoArgs();
				args.TrackInfo = ti;
				handler(this, args);
			}
		}
	}
	
	public class SqlLoadTransaction : LibraryTransaction
	{
		private string sql;
		
		public event HaveTrackInfoHandler HaveTrackInfo;
		
		public override string Name {
			get {
				return "Library Track Loader";
			}
		}
		
		public SqlLoadTransaction(string sql)
		{
			this.sql = sql;
		}
		
		public SqlLoadTransaction(Statement sql)
		{
			this.sql = sql.ToString();
		}
		
		public override void Run()
		{
			totalCount = 0;
			currentCount = 0;
			statusMessage = "Processing";
			AddSql();
		}
		
		private void RaiseTrackInfo(TrackInfo ti)
		{
			statusMessage = "Loading " + ti.Artist + " - " + ti.Title;
			currentCount++;
			
			HaveTrackInfoHandler handler = HaveTrackInfo;
			if(handler != null) {
				HaveTrackInfoArgs args = new HaveTrackInfoArgs();
				args.TrackInfo = ti;
				handler(this, args);
			}
		}
		
		private void AddSql()
		{
			totalCount = SqlCount();
			IDataReader reader = Core.Library.Db.Query(sql);
			while(reader.Read() && !cancelRequested) {
				DateTime startStamp = DateTime.Now;
				RaiseTrackInfo(new TrackInfo(reader));
				UpdateAverageDuration(startStamp);
			}
		}
		
		private long SqlCount()
		{
			string countQuery = Regex.Replace(sql,
				"SELECT (.*) FROM",
				"SELECT COUNT($1) FROM"); 
				
			long count;
			
			try {
				count = Convert.ToInt64(Core.Library.Db.QuerySingle(countQuery));
			} catch(Exception) {
				count = 0;
			}

			return count;
		}
	}
}
