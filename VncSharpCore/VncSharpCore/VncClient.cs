// VncSharp - .NET VNC Client Library
// Copyright (C) 2008 David Humphrey
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation; either version 2 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA

using System;
using System.Diagnostics;
using System.Drawing;
using System.Media;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// ReSharper disable CompareOfFloatsByEqualityOperator
// ReSharper disable ArrangeAccessorOwnerBody

namespace VncSharpCore
{
	/// <summary>
	/// Delegate definition of an Event Handler used to indicate a Framebuffer Update has been received.
	/// </summary>
	public delegate void VncUpdateHandler(object sender, VncEventArgs e);
	
	public class VncClient
	{
		private RfbProtocol rfb;			// The protocol object handling all communication with server.
		private byte securityType;			// The type of Security agreed upon by client/server
		private EncodedRectangleFactory factory;
		private Thread worker;				// To request and read in-coming updates from server
		private ManualResetEvent done;		// Used to tell the worker thread to die cleanly
		private IVncInputPolicy inputPolicy;// A mouse/keyboard input strategy
		private bool viewOnlyMode = false;

		/// <summary>
		/// Raised when the connection to the remote host is lost.
		/// </summary>
		public event EventHandler ConnectionLost;

		/// <summary>
		/// Raised when the server caused the local clipboard to be filled.
		/// </summary>
		public event EventHandler ServerCutText;

		/// <summary>
		/// Gets the Framebuffer representing the remote server's desktop geometry.
		/// </summary>
		public Framebuffer Framebuffer { get; private set; }

		/// <summary>
		/// Gets/Sets if full screen refresh is required (forced) from the connected server.
		/// </summary>
		public bool FullScreenRefresh { get; set; }

		/// <summary>
		/// Gets the hostname of the remote desktop
		/// </summary>
		public string HostName
		{
			get { return Framebuffer.DesktopName; }
		}

		/// <summary>
		/// Returns True if the VncClient object is View-Only, meaning no mouse/keyboard events are being sent.
		/// </summary>
		public bool ViewOnly
	    {
	        get {	return viewOnlyMode;	}
			set {	viewOnlyMode = value;
					// Allocate the inputPolicy if there is a connection
					if (rfb != null)
					{	if (viewOnlyMode)
							inputPolicy = new VncViewInputPolicy(rfb);
						else
							inputPolicy = new VncDefaultInputPolicy(rfb);
					}
				}
	    }

	    // Just for API compat, since I've added viewOnly
		public bool Connect(string host, int display, int port)
		{
			return Connect(host, display, port, viewOnlyMode);
		}

		/// <summary>
		/// Connect to a VNC Host and determine which type of Authentication it uses. If the host uses Password Authentication, a call to Authenticate() will be required.
		/// </summary>
		/// <param name="host">The IP Address or Host Name of the VNC Host.</param>
		/// <param name="display">The Display number (used on Unix hosts).</param>
		/// <param name="port">The Port number used by the Host, usually 5900.</param>
		/// <param name="viewOnly">True if mouse/keyboard events are to be ignored.</param>
		/// <returns>Returns True if the VNC Host requires a Password to be sent after Connect() is called, otherwise False.</returns>
		public bool Connect(string host, int display, int port, bool viewOnly)
		{
			if (host == null) throw new ArgumentNullException(nameof(host));

			// If a diplay number is specified (used to connect to Unix servers)
			// it must be 0 or greater.  This gets added to the default port number
			// in order to determine where the server will be listening for connections.
			if (display < 0) throw new ArgumentOutOfRangeException(nameof(display), display, "Display number must be non-negative.");
			port += display;
			
			rfb = new RfbProtocol();

			viewOnlyMode = viewOnly;
			if (viewOnly) {
				inputPolicy = new VncViewInputPolicy(rfb);
			} else {
				inputPolicy = new VncDefaultInputPolicy(rfb);
			}
			
			// Connect and determine version of server, and set client protocol version to match			
			try {
				rfb.Connect(host, port);
				rfb.ReadProtocolVersion();

				// Handle possible repeater connection
				if (rfb.ServerVersion == 0.0) {
					rfb.WriteProxyAddress();
					// Now we are connected to the real server; read the protocol version of the 
					// server
					rfb.ReadProtocolVersion();
					// Resume normal handshake and protocol
				}
					
				rfb.WriteProtocolVersion();

				// Figure out which type of authentication the server uses
				var types = rfb.ReadSecurityTypes();
				
				// Based on what the server sends back in the way of supported Security Types, one of
				// two things will need to be done: either the server will reject the connection (i.e., type = 0),
				// or a list of supported types will be sent, of which we need to choose and use one.
			    if (types.Length <= 0)
                    // Something is wrong, since we should have gotten at least 1 Security Type
                    throw new VncProtocolException(
			            "Protocol Error Connecting to Server. The Server didn't send any Security Types during the initial handshake.");
			    if (types[0] == 0) {
			        // The server is not able (or willing) to accept the connection.
			        // A message follows indicating why the connection was dropped.
			        throw new VncProtocolException("Connection Failed. The server rejected the connection for the following reason: " + rfb.ReadSecurityFailureReason());
			    }
			    securityType = GetSupportedSecurityType(types);
				Debug.Assert(securityType > 0, "Unknown Security Type(s)", "The server sent one or more unknown Security Types.");
						
			    rfb.WriteSecurityType(securityType);
						
			    // Protocol 3.8 states that a SecurityResult is still sent when using NONE (see 6.2.1)
			    if (rfb.ServerVersion != 3.8f || securityType != 1) return securityType > 1;
			    if (rfb.ReadSecurityResult() > 0) {
			        // For some reason, the server is not accepting the connection.  Get the
			        // reason and throw an exception
			        throw new VncProtocolException("Unable to Connecto to the Server. The Server rejected the connection for the following reason: " + rfb.ReadSecurityFailureReason());
			    }

			    return securityType > 1;
			    
			} catch (Exception e) {
				throw new VncProtocolException("Unable to connect to the server. Error was: " + e.Message, e);
			}			
		}

		/// <summary>
		/// Connect to a VNC Host and determine which type of Authentication it uses. If the host uses Password Authentication, a call to Authenticate() will be required. Default Display and Port numbers are used.
		/// </summary>
		/// <param name="host">The IP Address or Host Name of the VNC Host.</param>
		/// <returns>Returns True if the VNC Host requires a Password to be sent after Connect() is called, otherwise False.</returns>
		public bool Connect(string host)
		{
			return Connect(host, 0, 5900);
		}

		/// <summary>
		/// Connect to a VNC Host and determine which type of Authentication it uses. If the host uses Password Authentication, a call to Authenticate() will be required. The Port number is calculated based on the Display.
		/// </summary>
		/// <param name="host">The IP Address or Host Name of the VNC Host.</param>
		/// <param name="display">The Display number (used on Unix hosts).</param>
		/// <returns>Returns True if the VNC Host requires a Password to be sent after Connect() is called, otherwise False.</returns>
		public bool Connect(string host, int display)
		{
			return Connect(host, display, 5900);
		}

		/// <summary>
		/// Examines a list of Security Types supported by a VNC Server and chooses one that the Client supports.  See 6.1.2 of the RFB Protocol document v. 3.8.
		/// </summary>
		/// <param name="types">An array of bytes representing the Security Types supported by the VNC Server.</param>
		/// <returns>A byte that represents the Security Type to be used by the Client.</returns>
		private byte GetSupportedSecurityType(byte[] types)
		{
			// Pick the first match in the list of given types.  If you want to add support for new
			// security types, do it here:
			for (var i = 0; i < types.Length; ++i) {
				if (   types[i] == 1  	// None
					|| types[i] == 2	// VNC Authentication
// TODO: None of the following are currently supported -------------------
//					|| types[i] == 5	// RA2
//					|| types[i] == 6    // RA2ne
//					|| types[i] == 16   // Tight
//					|| types[i] == 17 	// Ultra
//					|| types[i] == 18 	// TLS
				   ) return types[i];
			}
			return 0;
		}

		/// <summary>
		/// Use a password to authenticate with a VNC Host. NOTE: This is only necessary if Connect() returns TRUE.
		/// </summary>
		/// <param name="password">The password to use.</param>
		/// <returns>Returns True if Authentication worked, otherwise False.</returns>
		public bool Authenticate(string password)
		{
			if (password == null) throw new ArgumentNullException(nameof(password));
			
			// If new Security Types are supported in future, add the code here.  For now, only 
			// VNC Authentication is supported.
			if (securityType == 2) {
				PerformVncAuthentication(password);
			} else {
				throw new NotSupportedException("Unable to Authenticate with Server. The Server uses an Authentication scheme unknown to the client.");
			}
			
			if (rfb.ReadSecurityResult() == 0) {
				return true;
			}
		    // Authentication failed, and if the server is using Protocol version 3.8, a 
		    // plain text message follows indicating why the error happend.  I'm not 
		    // currently using this message, but it is read here to clean out the stream.
		    // In earlier versions of the protocol, the server will just drop the connection.
		    if (rfb.ServerVersion == 3.8) rfb.ReadSecurityFailureReason();
		    rfb.Close();	// TODO: Is this the right place for this???
		    return false;
		}

		/// <summary>
		/// Performs VNC Authentication using VNC DES encryption.  See the RFB Protocol doc 6.2.2.
		/// </summary>
		/// <param name="password">A string containing the user's password in clear text format.</param>
		private void PerformVncAuthentication(string password)
		{
			var challenge = rfb.ReadSecurityChallenge();
			rfb.WriteSecurityResponse(EncryptChallenge(password, challenge));
		}

		/// <summary>
		/// Encrypts a challenge using the specified password. See RFB Protocol Document v. 3.8 section 6.2.2.
		/// </summary>
		/// <param name="password">The user's password.</param>
		/// <param name="challenge">The challenge sent by the server.</param>
		/// <returns>Returns the encrypted challenge.</returns>
		private byte[] EncryptChallenge(string password, byte[] challenge)
		{
			var key = new byte[8];

			// Key limited to 8 bytes max.
		    Encoding.ASCII.GetBytes(password, 0, password.Length >= 8 ? 8 : password.Length, key, 0);

		    // VNC uses reverse byte order in key
            for (var i = 0; i < 8; i++)
                key[i] = (byte)( ((key[i] & 0x01) << 7) |
                                 ((key[i] & 0x02) << 5) |
                                 ((key[i] & 0x04) << 3) |
                                 ((key[i] & 0x08) << 1) |
                                 ((key[i] & 0x10) >> 1) |
                                 ((key[i] & 0x20) >> 3) |
                                 ((key[i] & 0x40) >> 5) |
                                 ((key[i] & 0x80) >> 7)  );

            // VNC uses DES, not 3DES as written in some documentation
            DES des = DES.Create();
            des.Padding = PaddingMode.None;
            des.Mode = CipherMode.ECB;
            var enc = des.CreateEncryptor(key, null); 

			var response = new byte[16];
			enc.TransformBlock(challenge, 0, challenge.Length, response, 0);
			
			return response;
		}

		/// <summary>
		/// Finish setting-up protocol with VNC Host.  Should be called after Connect and Authenticate (if password required).
		/// </summary>
		public void Initialize(int bitsPerPixel, int depth)
		{
			// Finish initializing protocol with host
			rfb.WriteClientInitialisation(true);  // Allow the desktop to be shared
			reqBpp = bitsPerPixel; reqDepth = depth; var startLow = QualityMode != VncQualityMode.High; currentLow = startLow; Framebuffer = rfb.ReadServerInit(startLow ? 16 : bitsPerPixel, startLow ? 16 : depth);

			rfb.WriteSetEncodings(EncodingsOverride ?? DefaultEncodings(startLow, new uint[] {	RfbProtocol.ZRLE_ENCODING,
			                                    RfbProtocol.HEXTILE_ENCODING, 
											//	RfbProtocol.CORRE_ENCODING, // CoRRE is buggy in some hosts, so don't bother using
												RfbProtocol.RRE_ENCODING,
												RfbProtocol.COPYRECT_ENCODING,
												RfbProtocol.RAW_ENCODING }));

			rfb.WriteSetPixelFormat(Framebuffer); trStart = System.Diagnostics.Stopwatch.GetTimestamp(); Tr("pixel format: " + Framebuffer.BitsPerPixel + " bpp" + ", screen " +Framebuffer.Width + "x" + Framebuffer.Height);	// set the required ramebuffer format
            
			// Create an EncodedRectangleFactory so that EncodedRectangles can be built according to set pixel layout
			factory = new EncodedRectangleFactory(rfb, Framebuffer);
		}

		/// <summary>
		/// Begin getting updates from the VNC Server.  This will continue until StopUpdates() is called.  NOTE: this must be called after Connect().
		/// </summary>
		public void StartUpdates()
		{
			// Start getting updates on background thread.
			worker = new Thread(GetRfbUpdates);
            // Bug Fix (Grégoire Pailler) for clipboard and threading
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = true;
			done = new ManualResetEvent(false);
			worker.Start();
		}

		/// <summary>
		/// Stops sending requests for updates and disconnects from the remote host.  You must call Connect() again if you wish to re-establish a connection.
		/// </summary>
		public void Disconnect()
		{
			// Stop the worker thread.
			if (done != null)
				done.Set();

			// BUG FIX: Simon.Phillips@warwick.ac.uk for UltraVNC disconnect issue
			// Request a tiny screen update to flush the blocking read
			try {
				rfb.WriteFramebufferUpdateRequest(0, 0, 1, 1, false);
			} catch {
				// this may not work, as Disconnect can get called in response to the
				// VncClient raising a ConnectionLost event (e.g., the remote host died).
			}
			if (worker != null)
				worker.Join(3000);	// this number is arbitrary, just so that it doesn't block forever....

			rfb.Close();	
			rfb = null;
		}

		/// <summary>
		/// An event that occurs whenever the server sends a Framebuffer Update.
		/// </summary>
		public event VncUpdateHandler VncUpdate;
		
		private long trStart = System.Diagnostics.Stopwatch.GetTimestamp();
		private void Tr(string m)
		{
			var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - trStart) * 1000 / System.Diagnostics.Stopwatch.Frequency;
			if (ms > 40000) return;   // detailed trace only in the first 40 s after connecting
			if (!TraceDetail && !(m.StartsWith("pixel format") || m.StartsWith("estimated speed") || m.StartsWith("quality change"))) return;   // one line per rectangle is too much for the everyday log
			DiagLog?.Invoke("TRACE +" + ms + " ms, received " +(rfb == null ? 0 : rfb.BytesReceived) + " B: " + m);
		}

		private bool CheckIfThreadDone()
		{
			return done.WaitOne(0, false);
		}
		
				// Diagnostics: where the time of each screen update goes (Stopwatch ticks) and which encodings the server sends
		private long statWait, statRead, statUi;
		private readonly long[] statEncRects = new long[32];
		private readonly long[] statEncPixels = new long[32];
		private long statBytesStart;
		private long statStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();

		private void CountEnc(int enc, Rectangle r)
		{
			var i = enc & 31;
			statEncRects[i]++;
			statEncPixels[i] += (long)r.Width * r.Height;
		}

		// Large rectangles (full screen) take many seconds to arrive on slow connections: they are drawn in strips as the tiles are ready.
		private bool ArmStrips(VncSharpCore.Encodings.ZrleRectangle z)
		{
			var r = z.UpdateRectangle;
			if (VncUpdate == null || r.Height <= 192 || (long)r.Width * r.Height <= 262144) return false;
			z.RowsDecoded = (y0, y1) => NotifyUpdate(new StripUpdater(z, y0, y1));
			return true;
		}

		private void NotifyUpdate(IDesktopUpdater updater)
		{
			if (CheckIfThreadDone() || VncUpdate == null) return;
			var e = new VncEventArgs(updater);
			var control = VncUpdate.Target as Control;
			var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
			try {
				if (control != null) control.Invoke(VncUpdate, this, e);
				else VncUpdate(this, e);
			} catch (Exception uex) {
				DiagLog?.Invoke("error in drawing the update (stream intact): " + uex.GetType().Name + ": " + uex.Message);
			} finally {
				statUi += System.Diagnostics.Stopwatch.GetTimestamp() - t0; Tr("    strip drawn in " +((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency) + " ms");
			}
		}

private static string EncName(int i)
		{
			switch (i) {
				case 0: return "Raw";
				case 1: return "CopyRect";
				case 2: return "RRE";
				case 4: return "CoRRE";
				case 5: return "Hextile";
				case 6: return "Zlib";
				case 7: return "Tight";
				case 16: return "ZRLE";
				default: return "enc." + i;
			}
		}

		/// <summary>summary since the last call: bytes received, where the time went and encodings used; then resets the statistics</summary>
		public string TakeStatsReport()
		{
			var now = System.Diagnostics.Stopwatch.GetTimestamp();
			var total = Math.Max(1L, now - statStartTicks);
			var secs = total / (double)System.Diagnostics.Stopwatch.Frequency;
			var bytes = rfb == null ? 0 : rfb.BytesReceived;
			var mb = (bytes - statBytesStart) / 1048576.0;
			Func<long, string> pct = t => (100.0 * t / total).ToString("0") + "%";
			var sb = new System.Text.StringBuilder();
			for (var i = 0; i < 32; i++) {
				if (statEncRects[i] == 0) continue;
				if (sb.Length > 0) sb.Append(", ");
				sb.Append(EncName(i)).Append(' ').Append(statEncRects[i]).Append(" rect. ").Append((statEncPixels[i] / 1000000.0).ToString("0.0")).Append(" Mpx");
			}
			var report = "network: " + mb.ToString("0.0") + " MB in " + secs.ToString("0") + " s (" + (mb * 8 / Math.Max(0.001, secs)).ToString("0.0") + " Mbit/s); time: waiting for the server " + pct(statWait)
				+ ", read+decode " + pct(statRead) + ", drawing on the UI " + pct(statUi) + "; encodings: " + (sb.Length == 0 ? "none" : sb.ToString());
			statWait = statRead = statUi = 0;
			Array.Clear(statEncRects, 0, 32);
			Array.Clear(statEncPixels, 0, 32);
			statBytesStart = bytes;
			statStartTicks = now;
			return report;
		}

		// Diagnostics: the application sets DiagLog to write in its own log what went wrong in the VNC stream
		public static Action<string> DiagLog; public static uint[] EncodingsOverride;
		/// <summary>also offers Tight (after ZRLE): used by servers that do not have ZRLE, such as TightVNC</summary>
		public static bool UseTight = true; public static bool TraceDetail = false;   // true: one line for each rectangle (tests only)
		/// <summary>JPEG quality requested from the server for Tight: 0 (minimum) - 9 (maximum)</summary>
		public static int TightJpegQuality = 7;
		/// <summary>zlib compression requested from the server for Tight: 0 (none) - 9 (maximum, more work for the server)</summary>
		public static int TightCompressLevel = 6;
		/// <summary>minimum milliseconds between two update requests (0 = immediately, even before decoding the previous one). With a little waiting the server merges more changes into a single send.</summary>
		public static int MinUpdateIntervalMs = 0;

		/// <summary>how to choose the quality: set before Initialize</summary>
		public VncQualityMode QualityMode = VncQualityMode.High;
		/// <summary>Tight JPEG quality at the "low" level (0-9)</summary>
		public static int TightJpegQualityLow = 3;
		/// <summary>in Automatic: above this speed (Mbit/s, measured on large updates) it switches to high quality</summary>
		public static double UpgradeMbit = 12;
		/// <summary>in Automatic: below this speed it goes back to low quality</summary>
		public static double DowngradeMbit = 4;

		private int reqBpp, reqDepth, outstanding;
		private volatile int pendingLevel = -1;   // -1 nothing to do, 0 = go high, 1 = go low
		private bool currentLow;
		private double tput; private int bigSamples;
		private long lastSwitchTicks = System.Diagnostics.Stopwatch.GetTimestamp();
		private long holdUntilTicks;

		/// <summary>true if the current quality is the low one</summary>
		public bool IsLowQuality { get { return currentLow; } }

		/// <summary>requests the quality change: it happens as soon as there is no open request</summary>
		public void RequestQuality(bool low) { pendingLevel = low ? 1 : 0; NudgeLater(); }

		/// <summary>true: the next request is sent as soon as the update header is read (faster but with more open requests). Changing quality during a session needs one request at a time, so the default is false.</summary>
		public static bool EarlyRequests = false;

		/// <summary>changes mode (Automatic / Low / High) during a session: Low and High change the format immediately, Automatic restarts from the current level and adapts it to the line</summary>
		public void SetQualityMode(VncQualityMode mode)
		{
			QualityMode = mode;
			tput = 0; bigSamples = 0; holdUntilTicks = 0;
			lastSwitchTicks = System.Diagnostics.Stopwatch.GetTimestamp();
			if (mode == VncQualityMode.Low && !currentLow) pendingLevel = 1;
			else if (mode == VncQualityMode.High && currentLow) pendingLevel = 0;
			else pendingLevel = -1;
			if (pendingLevel >= 0) NudgeLater();
		}

		/// <summary>minimal non-incremental request (1x1): the server replies immediately and the read loop can apply the change even when the screen is still</summary>
		private bool nudgeSent, markerSeen, staleMarkerPending;
		private EncodedRectangleFactory oldFactory;
		private long staleTicks;

		private static bool IsMarker(Rectangle r) { return r.X == 3 && r.Y == 5 && r.Width == 1 && r.Height == 1; }

		/// <summary>Chooses how to decode a rectangle. The "wake-up" rectangle may still arrive in the format from before the change (the server prepared it before reading the change): in that case the old factory is used.</summary>
		private EncodedRectangleFactory PickFactory(Rectangle r)
		{
			if (staleMarkerPending && (System.Diagnostics.Stopwatch.GetTimestamp() - staleTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency > 3000) staleMarkerPending = false;
			if (IsMarker(r))
			{
				if (staleMarkerPending && oldFactory != null) { staleMarkerPending = false; return oldFactory; }
				if (nudgeSent) markerSeen = true;
			}
			return factory;
		}

		private System.Threading.Timer nudgeTimer;
		private void NudgeLater()
		{
			if (nudgeTimer != null) nudgeTimer.Dispose();
			nudgeTimer = new System.Threading.Timer(delegate { if (pendingLevel >= 0) Nudge(); }, null, 2000, System.Threading.Timeout.Infinite);
		}

		private void Nudge()
		{
			nudgeSent = true; markerSeen = false; try { rfb.WriteFramebufferUpdateRequest(3, 5, 1, 1, false); } catch { /* connection closed: the read loop reports it */ }
		}

		private static uint[] BasicSet()
		{
			return new uint[] { RfbProtocol.ZRLE_ENCODING, RfbProtocol.HEXTILE_ENCODING, RfbProtocol.RRE_ENCODING, RfbProtocol.COPYRECT_ENCODING, RfbProtocol.RAW_ENCODING };
		}

		/// <summary>Changes pixel format and encodings during a session. Called only with zero open requests: no update in flight in the old format.</summary>
		private void ApplyLevel(bool low)
		{
			var old = Framebuffer; oldFactory = factory; staleMarkerPending = nudgeSent && !markerSeen; staleTicks = System.Diagnostics.Stopwatch.GetTimestamp(); nudgeSent = false; markerSeen = false;
			var fb = Framebuffer.FromPixelFormat(rfb.ServerPixelFormat, old.Width, old.Height, low ? 16 : reqBpp, low ? 16 : reqDepth);
			fb.DesktopName = old.DesktopName;
			rfb.WriteSetEncodings(EncodingsOverride ?? DefaultEncodings(low, BasicSet()));
			rfb.WriteSetPixelFormat(fb);
			Framebuffer = fb;
			factory = new EncodedRectangleFactory(rfb, fb);
			currentLow = low;
			tput = 0; bigSamples = 0;
			lastSwitchTicks = System.Diagnostics.Stopwatch.GetTimestamp();
			if (!low) FullScreenRefresh = true;   // when quality goes up everything is redrawn in place (the previous image stays on screen)
			DiagLog?.Invoke("quality: now " + (low ? "LOW" : "HIGH") + " (" + fb.BitsPerPixel + " bit)");
			Tr("quality change applied: " + (low ? "low" : "high"));
		}

		/// <summary>measures the speed on large updates and decides whether to change quality (Automatic only)</summary>
		private void Observe(long bytes, long ticks)
		{
			if (QualityMode != VncQualityMode.Auto) return;
			var f = System.Diagnostics.Stopwatch.Frequency;
			var now = System.Diagnostics.Stopwatch.GetTimestamp();
			var ms = ticks * 1000 / f;
			var sinceSwitch = (now - lastSwitchTicks) * 1000 / f;
			if (bytes >= 150000 && ms > 0)
			{
				var mbit = bytes * 8.0 / 1000.0 / ms;
				tput = bigSamples == 0 ? mbit : 0.5 * tput + 0.5 * mbit;
				bigSamples++;
				Tr("estimated speed " + tput.ToString("0.0") + " Mbit/s (update of " + bytes + " bytes in " + ms + " ms)");
			}
			if (pendingLevel >= 0 || now < holdUntilTicks) return;
			if (currentLow)
			{
				var enough = bigSamples > 0 ? tput >= UpgradeMbit : sinceSwitch >= 5000;
				if (enough && sinceSwitch >= 1000)
				{
					pendingLevel = 0;
					DiagLog?.Invoke("quality: the line keeps up (" + tput.ToString("0.0") + " Mbit/s), switching to HIGH");
				}
			}
			else if (bigSamples > 0 && bytes >= 300000 && ms >= 800 && tput < DowngradeMbit && sinceSwitch >= 8000)
			{
				pendingLevel = 1;
				holdUntilTicks = now + 20 * f;   // after a downgrade, wait before trying to go up again
				DiagLog?.Invoke("quality: the line does not keep up (" + tput.ToString("0.0") + " Mbit/s), switching to LOW");
			}
		}
		private long lastRequestTicks;
		private void ThrottleRequest()
		{
			if (MinUpdateIntervalMs <= 0) return;
			var elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - lastRequestTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency;
			var wait = MinUpdateIntervalMs - elapsed;
			if (wait > 0) Thread.Sleep((int)wait);
		}

		private static uint[] DefaultEncodings(bool low, uint[] basic)
		{
			if (!UseTight) return basic;
			var l = new System.Collections.Generic.List<uint>();
			l.Add(basic[0]);                                  // ZRLE stays first: servers that have it use the proven path
			l.Add((uint)RfbProtocol.TIGHT_ENCODING);
			for (var i = 1; i < basic.Length; i++) l.Add(basic[i]);
			l.Add(unchecked((uint)(-32 + (low ? TightJpegQualityLow : TightJpegQuality))));        // pseudo-encoding: JPEG quality
			l.Add(unchecked((uint)(-256 + (low ? 9 : TightCompressLevel))));     // pseudo-encoding: compression level
			return l.ToArray();
		}   // tests only: encodings to offer the server instead of the normal list

		/// <summary>last text received from the server clipboard (the read thread cannot write to the Windows clipboard: the application does it)</summary>
		public string LastServerCutText { get; private set; } = "";

		/// <summary>
		/// Worker thread lives here and processes protocol messages infinitely, triggering events or other actions as necessary.
		/// </summary>
		private void GetRfbUpdates()
		{
			// Get the initial destkop from the host
			int connLostCount = 0;
			RequestScreenUpdate(true);

			while (true) {
				if (CheckIfThreadDone())
					break;

                var inMessage = false; var requestedEarly = false;
                try {
                    var tWait = System.Diagnostics.Stopwatch.GetTimestamp(); var msgType = rfb.ReadServerMessageType(); statWait += System.Diagnostics.Stopwatch.GetTimestamp() - tWait;   // a timeout here is harmless: no message incoming
                    inMessage = true; var qT0 = System.Diagnostics.Stopwatch.GetTimestamp(); var qB0 = rfb.BytesReceived; Tr("message type " + msgType + " (waited " +((System.Diagnostics.Stopwatch.GetTimestamp() - tWait) * 1000 / System.Diagnostics.Stopwatch.Frequency) + " ms)");                            // from here on an error leaves the stream in the middle of a message
                    // ReSharper disable once SwitchStatementMissingSomeCases
                    switch (msgType) {
                        case RfbProtocol.FRAMEBUFFER_UPDATE:
                            var rectangles = rfb.ReadFramebufferUpdate(); outstanding = outstanding > 0 ? outstanding - 1 : 0; Tr("update with " + rectangles + " rectangles");
                            if (!CheckIfThreadDone() && MinUpdateIntervalMs <= 0 && EarlyRequests) { RequestScreenUpdate(FullScreenRefresh); requestedEarly = true; }

                            if (CheckIfThreadDone())
                                break;

                            // TODO: consider gathering all update rectangles in a batch and *then* posting the event back to the main thread.
                            for (var i = 0; i < rectangles; ++i) {
                                // Get the update rectangle's info
                                rfb.ReadFramebufferUpdateRectHeader(out Rectangle rectangle, out int enc); var trB0 = rfb.BytesReceived; var trT0 = System.Diagnostics.Stopwatch.GetTimestamp(); Tr("  rect " + i + ": x=" +rectangle.X + " y=" + rectangle.Y + " " + rectangle.Width + "x" + rectangle.Height + " " + EncName(enc));

                                // Build a derived EncodedRectangle type and pull-down all the pixel info
                                var er = PickFactory(rectangle).Build(rectangle, enc);
                                var stripsDone = false; var zr0 = er as VncSharpCore.Encodings.ZrleRectangle; if (zr0 != null) stripsDone = ArmStrips(zr0); var tDec = System.Diagnostics.Stopwatch.GetTimestamp(); var uiBefore = statUi; er.Decode(); statRead += (System.Diagnostics.Stopwatch.GetTimestamp() - tDec) - (statUi - uiBefore); CountEnc(enc, rectangle); Tr("  rect " + i + ": read " + (rfb.BytesReceived - trB0) + " bytes, read+decode " + ((System.Diagnostics.Stopwatch.GetTimestamp() - trT0) * 1000 / System.Diagnostics.Stopwatch.Frequency) + " ms, in strips=" + stripsDone);

                                // Let the UI know that an updated rectangle is available, but check
                                // to see if the user closed things down first.
                                if (stripsDone || CheckIfThreadDone() || VncUpdate == null) continue;
                                var e = new VncEventArgs(er);

                                // In order to play nicely with WinForms controls, we do a check here to 
                                // see if it is necessary to synchronize this event with the UI thread.
                                var control = VncUpdate.Target as Control;
                                if (control != null) {
                                    try { var tUi = System.Diagnostics.Stopwatch.GetTimestamp(); try { control.Invoke(VncUpdate, this, e); } finally { statUi += System.Diagnostics.Stopwatch.GetTimestamp() - tUi; } } catch (Exception uex) { DiagLog?.Invoke("error in drawing the update (stream intact): " + uex.GetType().Name + ": " + uex.Message); }
                                } else {
                                    // Target is not a WinForms control, so do it on this thread...
                                    try { var tUi2 = System.Diagnostics.Stopwatch.GetTimestamp(); try { VncUpdate(this, new VncEventArgs(er)); } finally { statUi += System.Diagnostics.Stopwatch.GetTimestamp() - tUi2; } } catch (Exception uex) { DiagLog?.Invoke("error in the update handler (stream intact): " + uex.GetType().Name + ": " + uex.Message); }
                                }
                            }
                            Observe(rfb.BytesReceived - qB0, System.Diagnostics.Stopwatch.GetTimestamp() - qT0); break;
                        case RfbProtocol.BELL:
                            Beep();
                            break;
                        case RfbProtocol.SERVER_CUT_TEXT:
                            if (CheckIfThreadDone())
                                break;
                            // TODO: This is invasive, should there be a bool property allowing this message to be ignored?
                            var cutText = rfb.ReadServerCutText().Replace("\n", Environment.NewLine);
                            // only the application decides whether the text goes to this PC's clipboard (VncHost.OnServerClipboard): there the block
                            // during Claude's work and the "Copy file" capture apply. Writing it here bypassed them
                            LastServerCutText = cutText;
                            OnServerCutText();
                            break;
                        case RfbProtocol.SET_COLOUR_MAP_ENTRIES:
                            rfb.ReadColourMapEntry();
                            break;
                    }
                    // Moved screen update request here to prevent it being called multiple times
                    // This was the case when multiple rectangles were returned by the host
                    if (!requestedEarly)
                    {
                        // in Automatic only one open request at a time: messages other than updates do not generate more
                        if (!(!EarlyRequests && msgType != RfbProtocol.FRAMEBUFFER_UPDATE))
                        {
                            if (pendingLevel >= 0) { var goLow = pendingLevel == 1; pendingLevel = -1; ApplyLevel(goLow); }
                            ThrottleRequest();
                            RequestScreenUpdate(FullScreenRefresh);
                        }
                    }
                    connLostCount = 0;
                    
                } catch (Exception ex)
                {
                    DiagLog?.Invoke("server read: " + ex.GetType().Name + ": " + ex.Message + (inMessage ? " (in the middle of a message)" : " (waiting for the next message, " + (connLostCount + 1) + "/3)"));
                    if (inMessage)
                    {
                        // Continuing would read random bytes as message types and lengths (seen: 1-2 GB buffers for a nonexistent 'clipboard text').
                        // The stream is out of sync: the loss is reported and the loop exits; the library user reconnects with a clean stream.
                        DiagLog?.Invoke("stream out of sync: reconnection needed");
                        // a dropped network (socket closed or interrupted) and unintelligible data are two different things: the reason says which
                        var netErr = ex is System.IO.IOException or System.Net.Sockets.SocketException || ex.InnerException is System.Net.Sockets.SocketException;
                        Lost(netErr ? "connection dropped while an update was arriving" : "unintelligible data from the server (stream out of sync)", ex);
                        break;
                    }
                    // On the first time of no data being received we force a complete update
                    // This is for times when the server has no update, and caused the timeout.
                    if (connLostCount++ > 1)
                        Lost("no data from the server for 3 waits in a row", ex);
                    else
                        RequestScreenUpdate(true);
                }
                FullScreenRefresh = false;
            }
        }

		/// <summary>reason for the last drop (operation + error), for the application's Activity log: readable even with diagnostics off</summary>
		public string LastError { get; private set; } = "";
		private void Lost(string what, Exception ex)
		{
			LastError = what + ": " + (ex is System.IO.IOException { InnerException: { } inner } ? inner.Message : ex.Message);
			OnConnectionLost();
		}

	    private void OnConnectionLost()
		{
			// In order to play nicely with WinForms controls, we do a check here to 
			// see if it is necessary to synchronize this event with the UI thread.
		    if (!(ConnectionLost?.Target is Control)) return;
		    var target = (Control) ConnectionLost.Target;

		    if (target != null)
		        target.Invoke(ConnectionLost, this, EventArgs.Empty);
		    else
		        ConnectionLost(this, EventArgs.Empty);
		}

	    private void OnServerCutText()
        {
            // In order to play nicely with WinForms controls, we do a check here to 
            // see if it is necessary to synchronize this event with the UI thread.
            if (!(ServerCutText?.Target is Control)) return;
            var target = (Control) ServerCutText.Target;

            if (target != null)
                target.Invoke(ServerCutText, this, EventArgs.Empty);
            else
                ServerCutText(this, EventArgs.Empty);
        }

// There is no managed way to get a system beep (until Framework v.2.0). So depending on the platform, something external has to be called.
#if Win32
	    private static void Beep()
	    {
            SystemSounds.Beep.Play();
        }
#else
		private void Beep()	// bool just so it matches the NativeMethods API signature
		{
			// TODO: How to do this under Unix?
			System.Console.Write("Beep!");
		}
#endif

        public void WriteClientCutText(string text)
        {
            try {
                rfb.WriteClientCutText(text);
            } catch (Exception ex) {
                Lost("sending the clipboard", ex);
            }
        }

		// TODO: This needs to be pushed into the protocol rather than expecting keysym from the caller.
		public void WriteKeyboardEvent(uint keysym, bool pressed)
		{
			try {
				inputPolicy.WriteKeyboardEvent(keysym, pressed);
			} catch (Exception ex) {
				Lost("sending a key", ex);
			}
		}

		// TODO: This needs to be pushed into the protocol rather than expecting the caller to create the mask.
		public void WritePointerEvent(byte buttonMask, Point point)
		{
			try {
				inputPolicy.WritePointerEvent(buttonMask, point);
			} catch (Exception ex) {
				Lost("sending the mouse", ex);
			}
		}
		
		/// <summary>
		/// Requests that the remote host send a screen update.
		/// </summary>
		/// <param name="refreshFullScreen">TRUE if the entire screen should be refreshed, FALSE if only a partial region needs updating.</param>
		/// <remarks>RequestScreenUpdate needs to be called whenever the client screen needs to be updated to reflect the state of the remote 
		///	desktop.  Typically you only need to have a particular region of the screen updated and can still use the rest of the 
		/// pixels on the client-side (i.e., when moving the mouse pointer, only the area around the pointer changes).  Therefore, you should
		/// almost always set refreshFullScreen to FALSE.  If the client-side image becomes corrupted, call RequestScreenUpdate with
		/// refreshFullScreen set to TRUE to get the complete image sent again.
		/// </remarks>
		public void RequestScreenUpdate(bool refreshFullScreen)
		{
			try {
				outstanding++; lastRequestTicks = System.Diagnostics.Stopwatch.GetTimestamp(); Tr("update request " + (refreshFullScreen ? "FULL" : "incremental")); rfb.WriteFramebufferUpdateRequest(0, 0, (ushort) Framebuffer.Width, (ushort) Framebuffer.Height, !refreshFullScreen);
			} catch (Exception ex) {
				Lost("screen update request", ex);
			}
		}
	}
}