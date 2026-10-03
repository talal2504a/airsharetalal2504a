// TEMPORARY — sirf frontend dekhne ke liye. Baad me asli Program.cs aayega.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var app = builder.Build();

app.UseHttpsRedirection();
app.UseDefaultFiles();   // index.html ko root pe serve karta hai
app.UseStaticFiles();    // wwwroot ka CSS/JS/images serve karta hai
app.MapControllers();

app.Run();