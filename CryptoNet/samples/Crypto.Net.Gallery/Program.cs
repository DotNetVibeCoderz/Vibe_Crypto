using Crypto.Net.Gallery;

// Crypto.Net Gallery — interactive developer studio.
// Built by Gravicode Studios, led by Kang Fadhil.
//
// Safe sandbox: the Gallery never broadcasts transactions. Keys it generates are demo keys
// and live only in memory for the duration of a request.

var app = GalleryServer.Create(args);
Console.WriteLine("Crypto.Net Gallery — built by Gravicode Studios, led by Kang Fadhil");
app.Run();
