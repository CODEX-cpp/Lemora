namespace VncSharpCore
{
	/// <summary>How to choose the image quality: automatically (starts low and goes up if the line can handle it) or always low / always high.</summary>
	public enum VncQualityMode
	{
		/// <summary>starts at 16 bit and low JPEG, then switches to full color if the measured speed allows it; goes back down if the line cannot keep up</summary>
		Auto = 0,
		/// <summary>always 16 bit and low JPEG</summary>
		Low = 1,
		/// <summary>always the server's format (full color)</summary>
		High = 2,
	}
}
