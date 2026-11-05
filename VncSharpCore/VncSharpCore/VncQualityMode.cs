namespace VncSharpCore
{
	/// <summary>Come scegliere la qualita' dell'immagine: in modo automatico (parte bassa e sale se la linea regge) o sempre bassa / sempre alta.</summary>
	public enum VncQualityMode
	{
		/// <summary>parte a 16 bit e JPEG basso, poi passa a colori pieni se la velocita' misurata lo permette; torna giu' se la linea non regge</summary>
		Auto = 0,
		/// <summary>sempre 16 bit e JPEG basso</summary>
		Low = 1,
		/// <summary>sempre il formato del server (colori pieni)</summary>
		High = 2,
	}
}
