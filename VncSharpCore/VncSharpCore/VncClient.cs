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

			rfb.WriteSetPixelFormat(Framebuffer); trStart = System.Diagnostics.Stopwatch.GetTimestamp(); Tr("formato pixel: " + Framebuffer.BitsPerPixel + " bpp" + ", schermo " + Framebuffer.Width + "x" + Framebuffer.Height);	// set the required ramebuffer format
            
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
			if (ms > 40000) return;   // traccia dettagliata solo nei primi 40 s dopo il collegamento
			if (!TraceDetail && !(m.StartsWith("formato pixel") || m.StartsWith("velocita'") || m.StartsWith("cambio qualita"))) return;   // una riga per rettangolo e' troppo per il log di tutti i giorni
			DiagLog?.Invoke("TRACE +" + ms + " ms, ricevuti " + (rfb == null ? 0 : rfb.BytesReceived) + " B: " + m);
		}

		private bool CheckIfThreadDone()
		{
			return done.WaitOne(0, false);
		}
		
				// Diagnosi: dove passa il tempo di ogni aggiornamento dello schermo (tick di Stopwatch) e quali codifiche manda il server
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

		// Rettangoli grandi (schermo intero) arrivano in molti secondi su connessioni lente: si disegnano a fasce man mano che i tile sono pronti.
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
				DiagLog?.Invoke("errore nel disegno dell'aggiornamento (flusso integro): " + uex.GetType().Name + ": " + uex.Message);
			} finally {
				statUi += System.Diagnostics.Stopwatch.GetTimestamp() - t0; Tr("    striscia disegnata in " + ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency) + " ms");
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
				default: return "cod." + i;
			}
		}

		/// <summary>riassunto dall'ultima chiamata: byte ricevuti, dove e' passato il tempo e codifiche usate; poi azzera le statistiche</summary>
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
				sb.Append(EncName(i)).Append(' ').Append(statEncRects[i]).Append(" rett. ").Append((statEncPixels[i] / 1000000.0).ToString("0.0")).Append(" Mpx");
			}
			var report = "rete: " + mb.ToString("0.0") + " MB in " + secs.ToString("0") + " s (" + (mb * 8 / Math.Max(0.001, secs)).ToString("0.0") + " Mbit/s); tempo: attesa del server " + pct(statWait)
				+ ", lettura+decodifica " + pct(statRead) + ", disegno sull'interfaccia " + pct(statUi) + "; codifiche: " + (sb.Length == 0 ? "nessuna" : sb.ToString());
			statWait = statRead = statUi = 0;
			Array.Clear(statEncRects, 0, 32);
			Array.Clear(statEncPixels, 0, 32);
			statBytesStart = bytes;
			statStartTicks = now;
			return report;
		}

		// Diagnosi: l'applicazione imposta DiagLog per scrivere nel proprio log cosa e' andato storto nel flusso VNC
		public static Action<string> DiagLog; public static uint[] EncodingsOverride;
		/// <summary>propone anche Tight (dopo ZRLE): lo usano i server che non hanno ZRLE, come TightVNC</summary>
		public static bool UseTight = true; public static bool TraceDetail = false;   // true: una riga per ogni rettangolo (solo per le prove)
		/// <summary>qualita' JPEG chiesta al server per Tight: 0 (minima) - 9 (massima)</summary>
		public static int TightJpegQuality = 7;
		/// <summary>compressione zlib chiesta al server per Tight: 0 (nessuna) - 9 (massima, piu' lavoro per il server)</summary>
		public static int TightCompressLevel = 6;
		/// <summary>millisecondi minimi tra due richieste di aggiornamento (0 = subito, anche prima di decodificare il precedente). Con un po' di attesa il server unisce piu' modifiche in un solo invio.</summary>
		public static int MinUpdateIntervalMs = 0;

		/// <summary>come scegliere la qualita': si imposta prima di Initialize</summary>
		public VncQualityMode QualityMode = VncQualityMode.High;
		/// <summary>qualita' JPEG di Tight nel livello "basso" (0-9)</summary>
		public static int TightJpegQualityLow = 3;
		/// <summary>in Automatica: sopra questa velocita' (Mbit/s, misurata sugli aggiornamenti grandi) si passa a qualita' alta</summary>
		public static double UpgradeMbit = 12;
		/// <summary>in Automatica: sotto questa velocita' si torna a qualita' bassa</summary>
		public static double DowngradeMbit = 4;

		private int reqBpp, reqDepth, outstanding;
		private volatile int pendingLevel = -1;   // -1 niente da fare, 0 = vai ad alta, 1 = vai a bassa
		private bool currentLow;
		private double tput; private int bigSamples;
		private long lastSwitchTicks = System.Diagnostics.Stopwatch.GetTimestamp();
		private long holdUntilTicks;

		/// <summary>true se la qualita' attuale e' quella bassa</summary>
		public bool IsLowQuality { get { return currentLow; } }

		/// <summary>chiede il cambio di qualita': avviene appena non c'e' nessuna richiesta aperta</summary>
		public void RequestQuality(bool low) { pendingLevel = low ? 1 : 0; NudgeLater(); }

		/// <summary>true: la richiesta successiva parte appena si legge l'intestazione dell'aggiornamento (piu' veloce ma con piu' richieste aperte). Per cambiare qualita' a sessione aperta serve una richiesta alla volta, quindi di default e' false.</summary>
		public static bool EarlyRequests = false;

		/// <summary>cambia modalita' (Automatica / Bassa / Alta) a sessione aperta: Bassa e Alta cambiano subito il formato, Automatica riparte dal livello attuale e lo adatta alla linea</summary>
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

		/// <summary>richiesta minima non incrementale (1x1): il server risponde subito e il ciclo di lettura puo' applicare il cambio anche con lo schermo fermo</summary>
		private bool nudgeSent, markerSeen, staleMarkerPending;
		private EncodedRectangleFactory oldFactory;
		private long staleTicks;

		private static bool IsMarker(Rectangle r) { return r.X == 3 && r.Y == 5 && r.Width == 1 && r.Height == 1; }

		/// <summary>Sceglie come decodificare un rettangolo. Il rettangolo "di risveglio" puo' arrivare ancora nel formato di prima del cambio (il server l'ha preparato prima di leggere il cambio): in quel caso si usa la fabbrica vecchia.</summary>
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
			nudgeSent = true; markerSeen = false; try { rfb.WriteFramebufferUpdateRequest(3, 5, 1, 1, false); } catch { /* collegamento chiuso: lo segnala il ciclo di lettura */ }
		}

		private static uint[] BasicSet()
		{
			return new uint[] { RfbProtocol.ZRLE_ENCODING, RfbProtocol.HEXTILE_ENCODING, RfbProtocol.RRE_ENCODING, RfbProtocol.COPYRECT_ENCODING, RfbProtocol.RAW_ENCODING };
		}

		/// <summary>Cambia formato pixel e codifiche a sessione aperta. Si chiama solo con zero richieste aperte: nessun aggiornamento in volo nel formato vecchio.</summary>
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
			if (!low) FullScreenRefresh = true;   // salendo di qualita' si ridisegna tutto sul posto (l'immagine di prima resta a video)
			DiagLog?.Invoke("qualita': ora " + (low ? "BASSA" : "ALTA") + " (" + fb.BitsPerPixel + " bit)");
			Tr("cambio qualita' applicato: " + (low ? "bassa" : "alta"));
		}

		/// <summary>misura la velocita' sugli aggiornamenti grandi e decide se cambiare qualita' (solo in Automatica)</summary>
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
				Tr("velocita' stimata " + tput.ToString("0.0") + " Mbit/s (aggiornamento da " + bytes + " byte in " + ms + " ms)");
			}
			if (pendingLevel >= 0 || now < holdUntilTicks) return;
			if (currentLow)
			{
				var enough = bigSamples > 0 ? tput >= UpgradeMbit : sinceSwitch >= 5000;
				if (enough && sinceSwitch >= 1000)
				{
					pendingLevel = 0;
					DiagLog?.Invoke("qualita': la linea regge (" + tput.ToString("0.0") + " Mbit/s), passo ad ALTA");
				}
			}
			else if (bigSamples > 0 && bytes >= 300000 && ms >= 800 && tput < DowngradeMbit && sinceSwitch >= 8000)
			{
				pendingLevel = 1;
				holdUntilTicks = now + 20 * f;   // dopo una discesa si aspetta prima di riprovare a salire
				DiagLog?.Invoke("qualita': la linea non regge (" + tput.ToString("0.0") + " Mbit/s), passo a BASSA");
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
			l.Add(basic[0]);                                  // ZRLE resta il primo: i server che lo hanno usano il percorso collaudato
			l.Add((uint)RfbProtocol.TIGHT_ENCODING);
			for (var i = 1; i < basic.Length; i++) l.Add(basic[i]);
			l.Add(unchecked((uint)(-32 + (low ? TightJpegQualityLow : TightJpegQuality))));        // pseudo-codifica: qualita' JPEG
			l.Add(unchecked((uint)(-256 + (low ? 9 : TightCompressLevel))));     // pseudo-codifica: livello di compressione
			return l.ToArray();
		}   // solo prove: codifiche da proporre al server al posto dell'elenco normale

		/// <summary>ultimo testo ricevuto dagli appunti del server (il thread di lettura non puo' scrivere negli appunti di Windows: lo fa l'applicazione)</summary>
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
                    var tWait = System.Diagnostics.Stopwatch.GetTimestamp(); var msgType = rfb.ReadServerMessageType(); statWait += System.Diagnostics.Stopwatch.GetTimestamp() - tWait;   // un timeout qui e' benigno: nessun messaggio in arrivo
                    inMessage = true; var qT0 = System.Diagnostics.Stopwatch.GetTimestamp(); var qB0 = rfb.BytesReceived; Tr("messaggio tipo " + msgType + " (atteso " + ((System.Diagnostics.Stopwatch.GetTimestamp() - tWait) * 1000 / System.Diagnostics.Stopwatch.Frequency) + " ms)");                            // da qui in poi un errore lascia il flusso a meta' di un messaggio
                    // ReSharper disable once SwitchStatementMissingSomeCases
                    switch (msgType) {
                        case RfbProtocol.FRAMEBUFFER_UPDATE:
                            var rectangles = rfb.ReadFramebufferUpdate(); outstanding = outstanding > 0 ? outstanding - 1 : 0; Tr("aggiornamento con " + rectangles + " rettangoli");
                            if (!CheckIfThreadDone() && MinUpdateIntervalMs <= 0 && EarlyRequests) { RequestScreenUpdate(FullScreenRefresh); requestedEarly = true; }

                            if (CheckIfThreadDone())
                                break;

                            // TODO: consider gathering all update rectangles in a batch and *then* posting the event back to the main thread.
                            for (var i = 0; i < rectangles; ++i) {
                                // Get the update rectangle's info
                                rfb.ReadFramebufferUpdateRectHeader(out Rectangle rectangle, out int enc); var trB0 = rfb.BytesReceived; var trT0 = System.Diagnostics.Stopwatch.GetTimestamp(); Tr("  rett " + i + ": x=" + rectangle.X + " y=" + rectangle.Y + " " + rectangle.Width + "x" + rectangle.Height + " " + EncName(enc));

                                // Build a derived EncodedRectangle type and pull-down all the pixel info
                                var er = PickFactory(rectangle).Build(rectangle, enc);
                                var stripsDone = false; var zr0 = er as VncSharpCore.Encodings.ZrleRectangle; if (zr0 != null) stripsDone = ArmStrips(zr0); var tDec = System.Diagnostics.Stopwatch.GetTimestamp(); var uiBefore = statUi; er.Decode(); statRead += (System.Diagnostics.Stopwatch.GetTimestamp() - tDec) - (statUi - uiBefore); CountEnc(enc, rectangle); Tr("  rett " + i + ": letti " + (rfb.BytesReceived - trB0) + " byte, lettura+decodifica " + ((System.Diagnostics.Stopwatch.GetTimestamp() - trT0) * 1000 / System.Diagnostics.Stopwatch.Frequency) + " ms, a strisce=" + stripsDone);

                                // Let the UI know that an updated rectangle is available, but check
                                // to see if the user closed things down first.
                                if (stripsDone || CheckIfThreadDone() || VncUpdate == null) continue;
                                var e = new VncEventArgs(er);

                                // In order to play nicely with WinForms controls, we do a check here to 
                                // see if it is necessary to synchronize this event with the UI thread.
                                var control = VncUpdate.Target as Control;
                                if (control != null) {
                                    try { var tUi = System.Diagnostics.Stopwatch.GetTimestamp(); try { control.Invoke(VncUpdate, this, e); } finally { statUi += System.Diagnostics.Stopwatch.GetTimestamp() - tUi; } } catch (Exception uex) { DiagLog?.Invoke("errore nel disegno dell'aggiornamento (flusso integro): " + uex.GetType().Name + ": " + uex.Message); }
                                } else {
                                    // Target is not a WinForms control, so do it on this thread...
                                    try { var tUi2 = System.Diagnostics.Stopwatch.GetTimestamp(); try { VncUpdate(this, new VncEventArgs(er)); } finally { statUi += System.Diagnostics.Stopwatch.GetTimestamp() - tUi2; } } catch (Exception uex) { DiagLog?.Invoke("errore nel gestore dell'aggiornamento (flusso integro): " + uex.GetType().Name + ": " + uex.Message); }
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
                            LastServerCutText = cutText;   // l'applicazione lo copia negli appunti dal thread dell'interfaccia (qui il thread non puo' farlo)
                            try { Clipboard.SetDataObject(cutText, true); }
                            catch (Exception cex) { DiagLog?.Invoke("appunti del server non copiati: " + cex.GetType().Name + ": " + cex.Message); }
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
                        // in Automatica una sola richiesta aperta alla volta: i messaggi diversi dagli aggiornamenti non ne generano altre
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
                    DiagLog?.Invoke("lettura dal server: " + ex.GetType().Name + ": " + ex.Message + (inMessage ? " (a meta' di un messaggio)" : " (in attesa del prossimo messaggio, " + (connLostCount + 1) + "/3)"));
                    if (inMessage)
                    {
                        // Proseguire leggerebbe byte a caso come tipi e lunghezze di messaggio (visto: buffer da 1-2 GB per un 'testo degli appunti' inesistente).
                        // Il flusso e' sfasato: si segnala la perdita e si esce; chi usa la libreria si ricollega con un flusso pulito.
                        DiagLog?.Invoke("flusso sfasato: collegamento da rifare");
                        // rete caduta (socket chiuso o interrotto) e dati incomprensibili sono due cose diverse: il motivo lo dice
                        var netErr = ex is System.IO.IOException or System.Net.Sockets.SocketException || ex.InnerException is System.Net.Sockets.SocketException;
                        Lost(netErr ? "connessione interrotta mentre arrivava un aggiornamento" : "dati dal server non comprensibili (flusso sfasato)", ex);
                        break;
                    }
                    // On the first time of no data being received we force a complete update
                    // This is for times when the server has no update, and caused the timeout.
                    if (connLostCount++ > 1)
                        Lost("nessun dato dal server per 3 attese di fila", ex);
                    else
                        RequestScreenUpdate(true);
                }
                FullScreenRefresh = false;
            }
        }

		/// <summary>motivo dell'ultima caduta (operazione + errore), per il Registro attivita' dell'applicazione: si legge anche con la diagnosi spenta</summary>
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
                Lost("invio degli appunti", ex);
            }
        }

		// TODO: This needs to be pushed into the protocol rather than expecting keysym from the caller.
		public void WriteKeyboardEvent(uint keysym, bool pressed)
		{
			try {
				inputPolicy.WriteKeyboardEvent(keysym, pressed);
			} catch (Exception ex) {
				Lost("invio di un tasto", ex);
			}
		}

		// TODO: This needs to be pushed into the protocol rather than expecting the caller to create the mask.
		public void WritePointerEvent(byte buttonMask, Point point)
		{
			try {
				inputPolicy.WritePointerEvent(buttonMask, point);
			} catch (Exception ex) {
				Lost("invio del mouse", ex);
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
				outstanding++; lastRequestTicks = System.Diagnostics.Stopwatch.GetTimestamp(); Tr("richiesta aggiornamento " + (refreshFullScreen ? "COMPLETO" : "incrementale")); rfb.WriteFramebufferUpdateRequest(0, 0, (ushort) Framebuffer.Width, (ushort) Framebuffer.Height, !refreshFullScreen);
			} catch (Exception ex) {
				Lost("richiesta di aggiornamento dello schermo", ex);
			}
		}
	}
}