using FluentValidation;
using FluentValidation.AspNetCore;
using Ganss.Xss;
using HtmlBuilders;
using LiteDB;
using Markdig;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text.RegularExpressions;
using static HtmlBuilders.HtmlTags;
using Htmx;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Scriban.Parsing;
using Microsoft.AspNetCore.Mvc.RazorPages;
using static System.Runtime.InteropServices.JavaScript.JSType;

const string displayDateFormat = "MMMM dd, yyyy";
const string homePageName = "home-page";
const string htmlMime = "text/html";
var builder = WebApplication.CreateBuilder();
builder.Services
  .AddSingleton<Wiki>()
  .AddAntiforgery()
  .AddMemoryCache()
  .AddCors(options =>
  {
      options.AddDefaultPolicy(builder =>
      {
          builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
      });
  })
  .AddSession(options =>
  {
      options.Cookie.HttpOnly = true;
      options.Cookie.IsEssential = true;
      options.Cookie.Name = "WikiSession";
      options.IdleTimeout = TimeSpan.FromMinutes(20);
  })
  .AddDistributedMemoryCache()
  .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
  {
      options.LoginPath = "/";
      options.LogoutPath = "/";
      options.AccessDeniedPath = "/";
      options.Cookie.HttpOnly = true;
      options.Cookie.IsEssential = true;
      options.ExpireTimeSpan = TimeSpan.FromMinutes(20);
  });

builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();
app.UseAntiforgery();

// Load home page
app.MapGet("/", (Wiki wiki, IAntiforgery antiforgery, HttpContext context) =>
{
    return Results.Content(GetRootHTML(homePageName, isLoggedIn(context),antiforgery,context), htmlMime);
});

app.MapGet("/new-page", (string? pageName, Wiki wiki, HttpContext context, IAntiforgery antiforgery) =>
{
    if (string.IsNullOrEmpty(pageName))
        Results.Redirect("/");

    var page = ToKebabCase(pageName!);
    var wikiPage = wiki.GetPage(page);
    if(wikiPage != null)
        return Results.BadRequest("Page already exists");
    string side = $"""
    <div id="side" hx-swap-oob="innerHTML">
        <br>
        {AllPagesForEditing(wiki)}
        </div>
    """;
    var formHtml = BuildForm(new PageInput(null, page, "", null), antiforgery.GetAndStoreTokens(context));
    var titleHtml = Title.Append(page).ToHtmlString();
    return Results.Text(formHtml + side + titleHtml, htmlMime);
    // Copied from https://www.30secondsofcode.org/c-sharp/s/to-kebab-case
    string ToKebabCase(string str)
    {
        Regex pattern = new Regex(@"[A-Z]{2,}(?=[A-Z][a-z]+[0-9]*|\b)|[A-Z]?[a-z]+[0-9]*|[A-Z]|[0-9]+");
        return string.Join("-", pattern.Matches(str)).ToLower();
    }
});

// Edit a wiki page
app.MapGet("/edit", (string pageName, HttpContext context, Wiki wiki, IAntiforgery antiForgery, HttpRequest request) =>
{
    if (!request.IsHtmx())
    {
        return Results.Content(GetRootHTML($"edit?pageName={pageName}", isLoggedIn(context), antiForgery, context),htmlMime);
    }
    var page = wiki.GetPage(pageName);
    if (page == null)
        return Results.NotFound();
    var deleteButton = "";
    if(!pageName.Equals(homePageName, StringComparison.Ordinal))
        deleteButton = RenderDeletePageButton(page, antiForgery.GetAndStoreTokens(context));
    string side = $"""
    {deleteButton}
    <div id="side" hx-swap-oob="innerHTML">
        <br>
        {AllPagesForEditing(wiki)}
        </div>
    """;
    var formHtml = BuildForm(new PageInput(page.Id, page.Name, page.Content, null), antiForgery.GetAndStoreTokens(context));
    var attachments = RenderPageAttachmentsForEdit(page, antiForgery.GetAndStoreTokens(context));
    return Results.Text(formHtml+ attachments + side, htmlMime);
});

// Deal with attachment download
app.MapGet("/attachment", (string fileId, Wiki wiki) =>
{
    var file = wiki.GetFile(fileId);
    if (file == null)
      return Results.NotFound();

    app.Logger.LogInformation("Attachment " + file!.Value.meta.Id + " - " + file.Value.meta.Filename);
    var base64 = Convert.ToBase64String(file.Value.file);
    var mimeType = file.Value.meta.MimeType;
    var html = $"<img src='data:{mimeType};base64,{base64}' alt='{file.Value.meta.Filename}' />";
    return Results.Text(html, "text/html");
});

// Load a wiki page
app.MapGet("/{pageName}", (string pageName, HttpContext context, Wiki wiki, IAntiforgery antiForgery, HttpRequest request) =>
{
    if (!request.IsHtmx())
    {
        return Results.Content(GetRootHTML(pageName, isLoggedIn(context), antiForgery, context), htmlMime);
    }
    var page = wiki.GetPage(pageName);
    var editTag =isLoggedIn(context)? A.Attribute("hx-get", $"/edit?pageName={pageName}")
        .Attribute("hx-target", "#main")
        .Attribute("hx-push-url", "true")
        .Append("Edit"):Div;
    if (page != null)
    {
        return Results.Text(
    H1.Append(KebabToNormalCase(page.Name)).ToHtmlString() +
    RenderPageContent(page) +
    RenderPageAttachments(page) +
    Div.Class("last-modified")
        .Append("Last modified: " + page.LastModifiedUtc.ToString(displayDateFormat))
        .ToHtmlString() +
        Div.Id("editPage").ToHtmlString()+
        editTag.ToHtmlString() +
    AllPages(wiki) +
    Title.Append(KebabToNormalCase(page.Name)).ToHtmlString()
    , htmlMime);
    }
    else
    {
        return pageName.Equals(homePageName, StringComparison.OrdinalIgnoreCase)
        ? Results.Redirect("/new-page?pageName=home-page")
        : Results.NotFound($"Page {pageName} not found");
    }
    static string KebabToNormalCase(string txt)
    {
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(txt.Replace('-', ' '));
    }
});
app.MapPost("/login", async(HttpContext context, [FromForm] string username, [FromForm] string password, IAntiforgery antiforgery, Wiki wiki) =>
{
    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
    {
        var text = $"""<div class="alert alert-danger" hx-swap-oob="innerHTML" id="error" role="alert">Please fill all fields</div>""";
        return Results.Content(text,htmlMime);
    }

    var (isOk, user, ex) = wiki.AuthenticateUser(username, password);

    if (isOk)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, user!.Username)
        };
        var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var authProperties = new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(20)
        };
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(claimsIdentity), authProperties);
        return Results.Content(GetRootHTML(homePageName, isLoggedIn(context), antiforgery, context), htmlMime);
    }
    else
    {
        var token = antiforgery.GetAndStoreTokens(context);
        return Results.BadRequest("Wrong Username or Password");
    }
});
app.MapPost("/register", (HttpContext context, [FromForm] string username, [FromForm] string password, IAntiforgery antiforgery, Wiki wiki) =>
{
    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
    {
        var text = $"""<div class="alert alert-danger" role="alert">Please fill all fields</div>""";
        return Results.Content(text,htmlMime);
    }

    var (isOk, ex) = wiki.RegisterUser(username, password);

    if (isOk)
    {
        var text = $"""<div class="alert alert-success loginMessage" role="alert">Registeration Successful</div>""";
        return Results.Content(text,htmlMime);
    }
    else
    {
        var text = $"""<div class="alert alert-danger loginMessage" role="alert">{ex?.Message ?? "An error occurred while registering the user."}</div>""";
        return Results.Content(text,htmlMime);
    }
});
app.MapPost("/logout", async(HttpContext context,IAntiforgery antiforgery) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Content(GetRootHTML(homePageName, isLoggedIn(context), antiforgery, context), htmlMime);
});
// Delete a page
app.MapPost("/delete-page", ([FromForm] string id ,HttpContext context, Wiki wiki) =>
{
    if (StringValues.IsNullOrEmpty(id))
    {
        app.Logger.LogWarning($"Unable to delete page because form Id is missing");
        return Results.Redirect("/");
    }

    var (isOk, exception) = wiki.DeletePage(Convert.ToInt32(id), homePageName);

    if (!isOk && exception != null)
        app.Logger.LogError(exception, $"Error in deleting page id {id}");
    else if (!isOk)
        app.Logger.LogError($"Unable to delete page id {id}");

    return Results.Redirect($"/{homePageName}");
});

app.MapPost("/delete-attachment", ([FromForm] string id, [FromForm] string pageId , HttpContext context, Wiki wiki)=>
{
    if (StringValues.IsNullOrEmpty(id))
    {
        app.Logger.LogWarning($"Unable to delete attachment because form Id is missing");
        return Results.Redirect("/");
    }

    if (StringValues.IsNullOrEmpty(pageId))
    {
        app.Logger.LogWarning($"Unable to delete attachment because form PageId is missing");
        return Results.Redirect("/");
    }

    var (isOk, page, exception) = wiki.DeleteAttachment(Convert.ToInt32(pageId), id);

    if (!isOk)
    {
        if (exception != null)
            app.Logger.LogError(exception, $"Error in deleting page attachment id {id}");
        else
            app.Logger.LogError($"Unable to delete page attachment id {id}");

        if (page != null)
            return Results.Redirect($"/{page.Name}");
        else
            return Results.Redirect("/");
    }

    return Results.Redirect($"/edit?pageName={page!.Name}");
});

// Add or update a wiki page
app.MapPost("/add-page", ([FromForm] string Name, HttpContext context, Wiki wiki, IAntiforgery antiForgery) =>
{
    PageInput input = PageInput.From(context.Request.Form);

    var modelState = new ModelStateDictionary();
    var validator = new PageInputValidator(Name, homePageName);
    validator.Validate(input).AddToModelState(modelState, null);

    if (!modelState.IsValid)
    {
        string side = $"""
        {AllPages(wiki)}
        """;
        string form = BuildForm(input, antiForgery.GetAndStoreTokens(context), modelState);
        return Results.Text(form + side, htmlMime);
    }

    var (isOk, p, ex) = wiki.SavePage(input);
    if (!isOk)
    {
        app.Logger.LogError(ex, "Problem in saving page");
        return Results.Problem("Problem in saving page");
    }

    return Results.Redirect($"/{p!.Name}");
});

await app.RunAsync();

// End of the web part

static string AllPages(Wiki wiki)
{
    string html = $"""
    <div id="side" class="uk-width-1-5" hx-swap-oob="innerHTML">
        <span class="uk-label">Pages</span>
        <ul class="uk-list">
        {string.Join("", wiki.ListAllPages().OrderBy(x => x.Name).Select(x => $"""<li><a hx-get="/{x.Name}" hx-target="#main" hx-swap="innerHTML">{x.Name}</a></li>"""))}
        </ul>
    </div>
    """;
    return html;
}
static string AllPagesForEditing(Wiki wiki)
{
    static string KebabToNormalCase(string txt) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(txt.Replace('-', ' '));

    string html = $"""
        <span class="uk-label">Pages</span>
        <ul class="uk-list">
                {string.Join("",
          wiki.ListAllPages().OrderBy(x => x.Name)
            .Select(x => Li.Append(Div.Class("uk-inline")
                .Append(Span.Class("uk-form-icon").Attribute("uk-icon", "icon: copy"))
                .Append(Input.Text.Value($"[{KebabToNormalCase(x.Name)}](/{x.Name})").Class("uk-input uk-form-small").Style("cursor", "pointer").Attribute("onclick", "copyMarkdownLink(this);"))
            ).ToHtmlString()
          )
        )}
        </ul>
        """;
    return html;
}
static string RenderMarkdown(string str)
{
    var sanitizer = new HtmlSanitizer();
    return sanitizer.Sanitize(Markdown.ToHtml(str, new MarkdownPipelineBuilder().UseSoftlineBreakAsHardlineBreak().UseAdvancedExtensions().Build()));
}

static string RenderPageContent(Page page) => RenderMarkdown(page.Content);

static string RenderDeletePageButton(Page page, AntiforgeryTokenSet antiForgery)
{
    var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);
    HtmlTag id = Input.Hidden.Name("Id").Value(page.Id.ToString());
    var submit = Div.Style("margin-top", "20px").Append(Button.Class("uk-button uk-button-danger").Append("Delete Page"));

    var form = Form
               .Attribute("hx-post", "/delete-page")
               .Attribute("hx-target", "#main")
               .Attribute("hx-confirm", "Confirm Deletion?")
                 .Append(antiForgeryField)
                 .Append(id)
                 .Append(submit);

    return form.ToHtmlString();
}

static string RenderPageAttachmentsForEdit(Page page, AntiforgeryTokenSet antiForgery)
{
    if (page.Attachments.Count == 0)
        return string.Empty;

    var label = Span.Class("uk-label").Append("Attachments");
    var list = Ul.Class("uk-list");

    HtmlTag CreateEditorHelper(Attachment attachment) =>
      Span.Class("uk-inline")
          .Append(Span.Class("uk-form-icon").Attribute("uk-icon", "icon: copy"))
          .Append(Input.Text.Value($"[{attachment.FileName}](/attachment?fileId={attachment.FileId})")
            .Class("uk-input uk-form-small uk-form-width-large")
            .Style("cursor", "pointer")
            .Attribute("onclick", "copyMarkdownLink(this);")
          );

    static HtmlTag CreateDelete(int pageId, string attachmentId, AntiforgeryTokenSet antiForgery)
    {
        var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);
        var id = Input.Hidden.Name("Id").Value(attachmentId);
        var name = Input.Hidden.Name("PageId").Value(pageId.ToString());

        var submit = Button.Class("uk-button uk-button-danger uk-button-small").Append(Span.Attribute("uk-icon", "icon: close; ratio: .75;"));
        var form = Form
               .Style("display", "inline")
               .Attribute("hx-post", "/delete-attachment")
               .Attribute("hx-target", "#main")
               .Attribute("hx-confirm", "Confirm attachment deletion?")
                 .Append(antiForgeryField)
                 .Append(id)
                 .Append(name)
                 .Append(submit);

        return form;
    }

    foreach (var attachment in page.Attachments)
    {
        list = list.Append(Li
          .Append(CreateEditorHelper(attachment))
          .Append(CreateDelete(page.Id, attachment.FileId, antiForgery))
        );
    }
    return label.ToHtmlString() + list.ToHtmlString();
}

static string RenderPageAttachments(Page page)
{
    if (page.Attachments.Count == 0)
        return string.Empty;

    var label = Span.Class("uk-label").Append("Attachments");
    var list = Ul.Class("uk-list uk-list-disc");
    foreach (var attachment in page.Attachments)
    {
        list = list.Append(Li.Append(A.Attribute("hx-get",$"/attachment?fileId={attachment.FileId}").Attribute("hx-target","#imageModalBody").Attribute("data-bs-toggle","modal")
            .Attribute("data-bs-target","#imageModal")
            .Append(attachment.FileName)));
    }
    return label.ToHtmlString() + list.ToHtmlString();
}

// Build the wiki input form 
static string BuildForm(PageInput input, AntiforgeryTokenSet antiForgery, ModelStateDictionary? modelState = null)
{
    bool IsFieldOk(string key) => modelState.ContainsKey(key) && modelState[key]!.ValidationState == ModelValidationState.Invalid;

    var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);

    var nameField = Div
      .Append(Label.Class("uk-form-label").Append(nameof(input.Name)))
      .Append(Div.Class("uk-form-controls")
        .Append(Input.Text.Class("uk-input").Name("Name").Value(input.Name))
      );

    var contentField = Div
      .Append(Label.Class("uk-form-label").Append(nameof(input.Content)))
      .Append(Div.Class("uk-form-controls")
        .Append(Textarea.Name("Content").Class("uk-textarea").Append(input.Content))
      );

    var attachmentField = Div
      .Append(Label.Class("uk-form-label").Append(nameof(input.Attachment)))
      .Append(Div.Attribute("uk-form-custom", "target: true")
        .Append(Input.File.Name("Attachment"))
        .Append(Input.Text.Class("uk-input uk-form-width-large").Attribute("placeholder", "Click to select file").ToggleAttribute("disabled", true))
      );

    if (modelState != null && !modelState.IsValid)
    {
        if (IsFieldOk("Name"))
        {
            foreach (var er in modelState["Name"]!.Errors)
            {
                nameField = nameField.Append(Div.Class("uk-form-danger uk-text-small").Append(er.ErrorMessage));
            }
        }

        if (IsFieldOk("Content"))
        {
            foreach (var er in modelState["Content"]!.Errors)
            {
                contentField = contentField.Append(Div.Class("uk-form-danger uk-text-small").Append(er.ErrorMessage));
            }
        }
    }

    var submit = Div.Style("margin-top", "20px").Append(Button.Class("uk-button uk-button-primary").Append("Submit"));

    var form = Form
               .Class("uk-form-stacked")
               .Attribute("hx-post", $"/add-page")
               .Attribute("hx-target", "#main")
               .Attribute("hx-encoding", "multipart/form-data")
                 .Append(antiForgeryField)
                 .Append(nameField)
                 .Append(contentField)
                 .Append(attachmentField);

    if (input.Id != null)
    {
        HtmlTag id = Input.Hidden.Name("Id").Value(input.Id.ToString()!);
        form = form.Append(id);
    }

    form = form.Append(submit);

    return form.ToHtmlString();
}

static string GetRootHTML(string pageName, bool loggedIn, IAntiforgery antiforgery, HttpContext context)
{
    string loggedHTML;
    string addPage = loggedIn ? """
                            <div class="uk-navbar-item" hx-swap-oob="innerHTML" id="addPage">
                                <form hx-get="/new-page" hx-target="#main">
                                    <input class="uk-input uk-form-width-large" type="text" name="pageName"
                                        placeholder="Type desired page title here"></input>
                                    <input type="submit" class="uk-button uk-button-default" value="Add New Page">
                                </form>
                            </div>
        """ : """<div class="uk-navbar-item" hx-swap-oob="innerHTML" id="addPage"></div>""";
    if (loggedIn)
    {
        loggedHTML = GetLoggedInHTML();
    }
    else
    {
        loggedHTML = GetLoggedOutHTML(antiforgery, context);
    }
    string html = $$"""
                <!DOCTYPE html>

        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>home-page</title>
            <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/css/uikit.min.css" />
            <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" rel="stylesheet"
                integrity="sha384-QWTKZyjpPEjISv5WaRU9OFeRpok6YctnYmDr5pNlyT2bRjXh0JMhjY6hW+ALEwIH" crossorigin="anonymous">
            <link rel="stylesheet" href="https://unpkg.com/easymde/dist/easymde.min.css">
            <script src="https://unpkg.com/easymde/dist/easymde.min.js"></script>
            <script src="https://unpkg.com/htmx.org@1.9.12"
                integrity="sha384-ujb1lZYygJmzgSwoxRggbCHcjc0rB2XoQrxeTUQyRjrOnlCoYta87iKBWq3EsdM2"
                crossorigin="anonymous"></script>
            <style>
                .last-modified {font-size: small;
                }

                a:visited {color: blue;
                }
                a {
                color: blue !important;
                }
                a:hover {
                text-decoration: underline !important;
                }
                #errorLog {color: red !important;
                }
            </style>
        </head>
        <body>
            <nav class="uk-navbar-container">
                <div class="uk-container">
                    <div class="uk-navbar">
                        <div class="uk-navbar-left">
                            <ul class="uk-navbar-nav">
                                <li class="uk-active"><a hx-get="/home-page" hx-push-url="/" hx-target="#main"><span uk-icon="home"></span></a></li>
                            </ul>
                        </div>
                        <div class="uk-navbar-center" id="addPage">
                            {{addPage}}
                        </div>
                        <div class="uk-navbar-right" id="logStatus">
                            {{loggedHTML}}
                        </div>
                    </div>
                </div>
            </nav>

            <div class="uk-container" hx-get="/{{pageName}}" hx-trigger="load" hx-target="#main">
                <div uk-grid>
                    <div class="uk-width-4-5" id="main">
                    </div>
                    <div class="uk-width-1-5" id="side">
                    </div>
                </div>
            </div>
        <div class="modal fade" id="imageModal" tabindex="-1" aria-labelledby="imageModalLabel" aria-hidden="true">
          <div class="modal-dialog modal-dialog-centered">
            <div class="modal-content">
              <div class="modal-header">
                  <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
              </div>
              <div class="modal-body" id="imageModalBody">
              </div>
            </div>
          </div>
        </div>

            <script src="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/js/uikit.min.js"></script>
            <script src="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/js/uikit-icons.min.js"></script>


            <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js"
                integrity="sha384-YvpcrYf0tY3lHB60NNkmXc5s9fDVZLESaAA55NDzOxhy9GkcIdslK1eN7N6jIeHz"
                crossorigin="anonymous"></script>
        <script>
        var easyMDE;
        document.addEventListener("htmx:afterRequest", function (event) {
            if (event.detail.pathInfo.responsePath.includes("/new-page") || event.detail.pathInfo.responsePath.includes("/edit")) {
                easyMDE = new EasyMDE({
                    insertTexts: {
                        link: ["[", "]()"]
                    }
                });
            }
            if(event.detail.pathInfo.responsePath.includes("/login") && event.detail.xhr.status === 400){
                alert('Login failed: Wrong Username or Password');
                console.log('Login failed: Wrong Username or Password');
            }else if(event.detail.pathInfo.responsePath.includes("/login") || event.detail.pathInfo.responsePath.includes("/logout")){
                window.location.href="/";
                console.log('Login successful');
            }
        });

        function copyMarkdownLink(element) {
            element.select();
            document.execCommand("copy");
        }
        </script>
            <!-- Visual Studio Browser Link -->
            <script type="text/javascript" src="/_vs/browserLink" async="async" id="__browserLink_initializationData"
                data-requestId="307ad795c6aa42c8b50edf0cbbeb62b5" data-requestMappingFromServer="false"
                data-connectUrl="http://localhost:53665/4c1e7817f02a40ffb1b112f97f05a6b1/browserLink"></script>
            <!-- End Browser Link -->
            <script src="/_framework/aspnetcore-browser-refresh.js"></script>
        </body>

        </html>
        """;
    return html;
}
static string GetLoggedOutHTML(IAntiforgery antiforgery, HttpContext context)
{
    var token = antiforgery.GetAndStoreTokens(context);
    string html = $"""
                        <div id="logStatus" hx-swap-oob="innerHTML">
                <button class="uk-button uk-button-primary" data-bs-toggle="modal"
                    data-bs-target="#loginModal">Login</button>
                <!-- The Modal -->
                <div class="modal fade" id="loginModal">
                    <div class="modal-dialog modal-dialog-centered">
                        <div class="modal-content">
                            <!-- Modal Header -->
                            <div class="modal-header">
                                <h4 class="modal-title">Please Login.</h4>
                                <button type="button" class="btn-close" data-bs-dismiss="modal"
                                    aria-label="Close"></button>
                            </div>
                            <!-- Modal Body -->
                            <div class="modal-body">
                                <form class="uk-form" hx-post="/login" hx-target="#main">
                                    <input name="{token.FormFieldName}" type="hidden" value="{token.RequestToken}" />
                                    <div class="form-group">
                                        <label for="username">Username:</label>
                                        <input type="text" required minlength="4" class="form-control mt-2"
                                            id="usernameLogin" placeholder="Enter username" name="username">
                                    </div>
                                    <div class="form-group">
                                        <label for="pwd" class="mt-2">Password:</label>
                                        <input type="password" required minlength="8" class="form-control mt-2"
                                            id="pwdLogin" placeholder="Enter password" name="password">
                                    </div>
                                    <button type="submit" class="btn btn-primary mt-3">Login</button>
                                </form>
                                <div id="errorLog"></div>
                            </div>
                            <!-- Modal Footer -->
                        </div>
                    </div>
                </div>

                <!-- Button to Open the Modal -->
                <button class="uk-button uk-button-primary" data-bs-toggle="modal"
                    data-bs-target="#registerModal">Register</button>
                <!-- The Modal -->
                <div class="modal fade" id="registerModal">
                    <div class="modal-dialog modal-dialog-centered">
                        <div class="modal-content">
                            <!-- Modal Header -->
                            <div class="modal-header">
                                <h4 class="modal-title">Create a new account.</h4>
                                <button type="button" class="btn-close" data-bs-dismiss="modal"
                                    aria-label="Close"></button>
                            </div>
                            <!-- Modal Body -->
                            <div class="modal-body">
                                <form class="uk-form" hx-post="/register" hx-target=".error">
                                    <input name="{token.FormFieldName}" type="hidden" value="{token.RequestToken}" />
                                    <div class="form-group">
                                        <label for="username">Username:</label>
                                        <input type="text" required minlength="4" class="form-control mt-2"
                                            id="username" name="username" placeholder="Enter username" name="username">
                                    </div>
                                    <div class="form-group">
                                        <label for="pwd" class="mt-2">Password:</label>
                                        <input type="password" required minlength="8" class="form-control mt-2"
                                            id="pwd" name="password" placeholder="Enter password" name="pswd">
                                    </div>
                                    <button type="submit" class="btn btn-primary mt-3">Register</button>
                                    <div class="error"></div>
                                </form>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
            """;
    return html;
}

static string GetLoggedInHTML()
{
    string html = $"""
                <div id="logStatus" hx-swap-oob="innerHTML">
                    <button class="uk-button uk-button-primary" hx-post="/logout" hx-target="#main">Logout</button>
                </div>
                """;
    return html;
}

static bool isLoggedIn(HttpContext context)
{
    return context.User.Identity?.IsAuthenticated ?? false;
}
class Wiki
{
    DateTime Timestamp() => DateTime.UtcNow;

    const string PageCollectionName = "Pages";
    const string UserCollectionName = "Users";
    const string AllPagesKey = "AllPages";
    const double CacheAllPagesForMinutes = 30;

    readonly IWebHostEnvironment _env;
    readonly IMemoryCache _cache;
    readonly ILogger _logger;

    public Wiki(IWebHostEnvironment env, IMemoryCache cache, ILogger<Wiki> logger)
    {
        _env = env;
        _cache = cache;
        _logger = logger;
    }

    // Get the location of the LiteDB file.
    string GetDbPath() => Path.Combine(_env.ContentRootPath, "wiki.db");

    // List all the available wiki pages. It is cached for 30 minutes.
    public List<Page> ListAllPages()
    {
        var pages = _cache.Get(AllPagesKey) as List<Page>;

        if (pages != null)
            return pages;

        using var db = new LiteDatabase(GetDbPath());
        var coll = db.GetCollection<Page>(PageCollectionName);
        var items = coll.Query().ToList();
        var userColl = db.GetCollection<User>(UserCollectionName);
        userColl.EnsureIndex(x => x.Username);


        _cache.Set(AllPagesKey, items, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(CacheAllPagesForMinutes)));
        return items;
    }

    // Get a wiki page based on its path
    public Page? GetPage(string path)
    {
        using var db = new LiteDatabase(GetDbPath());
        var coll = db.GetCollection<Page>(PageCollectionName);
        coll.EnsureIndex(x => x.Name);

        return coll.Query()
                .Where(x => x.Name.Equals(path, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
    }

    // Save or update a wiki page. Cache(AllPagesKey) will be destroyed.
    public (bool isOk, Page? page, Exception? ex) SavePage(PageInput input)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<Page>(PageCollectionName);
            coll.EnsureIndex(x => x.Name);

            var existingPage = input.Id.HasValue ? coll.FindOne(x => x.Id == input.Id) : null;

            var sanitizer = new HtmlSanitizer();
            var properName = input.Name.Trim().Replace(' ', '-').ToLower();

            Attachment? attachment = null;
            if (!string.IsNullOrWhiteSpace(input.Attachment?.FileName))
            {
                attachment = new Attachment
                (
                    FileId: Guid.NewGuid().ToString(),
                    FileName: input.Attachment.FileName,
                    MimeType: input.Attachment.ContentType,
                    LastModifiedUtc: Timestamp()
                );

                using var stream = input.Attachment.OpenReadStream();
                _ = db.FileStorage.Upload(attachment.FileId, input.Attachment.FileName, stream);
            }

            if (existingPage == null)
            {
                var newPage = new Page
                {
                    Name = sanitizer.Sanitize(properName),
                    Content = input.Content, //Do not sanitize on input because it will impact some markdown tag such as >. We do it on the output instead.
                    LastModifiedUtc = Timestamp()
                };

                if (attachment != null)
                    newPage.Attachments.Add(attachment);

                coll.Insert(newPage);

                _cache.Remove(AllPagesKey);
                return (true, newPage, null);
            }
            else
            {
                var updatedPage = existingPage with
                {
                    Name = sanitizer.Sanitize(properName),
                    Content = input.Content, //Do not sanitize on input because it will impact some markdown tag such as >. We do it on the output instead.
                    LastModifiedUtc = Timestamp()
                };

                if (attachment != null)
                    updatedPage.Attachments.Add(attachment);

                coll.Update(updatedPage);

                _cache.Remove(AllPagesKey);
                return (true, updatedPage, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"There is an exception in trying to save page name '{input.Name}'");
            return (false, null, ex);
        }
    }

    public (bool isOk, Page? p, Exception? ex) DeleteAttachment(int pageId, string id)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<Page>(PageCollectionName);
            var page = coll.FindById(pageId);
            if (page == null)
            {
                _logger.LogWarning($"Delete attachment operation fails because page id {id} cannot be found in the database");
                return (false, null, null);
            }

            if (!db.FileStorage.Delete(id))
            {
                _logger.LogWarning($"We cannot delete this file attachment id {id} and it's a mystery why");
                return (false, page, null);
            }

            page.Attachments.RemoveAll(x => x.FileId.Equals(id, StringComparison.OrdinalIgnoreCase));

            var updateResult = coll.Update(page);

            if (!updateResult)
            {
                _logger.LogWarning($"Delete attachment works but updating the page (id {pageId}) attachment list fails");
                return (false, page, null);
            }

            return (true, page, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex);
        }
    }

    public (bool isOk, Exception? ex) DeletePage(int id, string homePageName)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<Page>(PageCollectionName);

            var page = coll.FindById(id);

            if (page == null)
            {
                _logger.LogWarning($"Delete operation fails because page id {id} cannot be found in the database");
                return (false, null);
            }

            if (page.Name.Equals(homePageName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning($"Page id {id}  is a home page and elete operation on home page is not allowed");
                return (false, null);
            }

            //Delete all the attachments
            foreach (var a in page.Attachments)
            {
                db.FileStorage.Delete(a.FileId);
            }

            if (coll.Delete(id))
            {
                _cache.Remove(AllPagesKey);
                return (true, null);
            }

            _logger.LogWarning($"Somehow we cannot delete page id {id} and it's a mistery why.");
            return (false, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    public (bool isOk, Exception? ex) RegisterUser(string username, string password)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<User>(UserCollectionName);
            coll.EnsureIndex(x => x.Username);

            if (coll.Exists(x => x.Username == username))
            {
                return (false, new Exception("Username already exists"));
            }

            var passwordHash = BCrypt.Net.BCrypt.HashPassword(password);

            var newUser = new User
            {
                Username = username,
                PasswordHash = passwordHash,
                CreatedAt = DateTime.UtcNow
            };

            coll.Insert(newUser);

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"There is an exception in trying to register user '{username}'");
            return (false, ex);
        }
    }

    // Authenticate a user
    public (bool isOk, User? user, Exception? ex) AuthenticateUser(string username, string password)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<User>(UserCollectionName);
            coll.EnsureIndex(x => x.Username);

            var user = coll.FindOne(x => x.Username == username);

            if (user == null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            {
                return (false, null, new Exception("Invalid username or password"));
            }

            return (true, user, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"There is an exception in trying to authenticate user '{username}'");
            return (false, null, ex);
        }
    }
    // Return null if file cannot be found.
    public (LiteFileInfo<string> meta, byte[] file)? GetFile(string fileId)
    {
        using var db = new LiteDatabase(GetDbPath());

        var meta = db.FileStorage.FindById(fileId);
        if (meta is null)
            return null;

        using var stream = new MemoryStream();
        db.FileStorage.Download(fileId, stream);
        return (meta, stream.ToArray());
    }
}
record Page
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime LastModifiedUtc { get; set; }

    public List<Attachment> Attachments { get; set; } = new();
}

record Attachment
(
    string FileId,

    string FileName,

    string MimeType,

    DateTime LastModifiedUtc
);

record PageInput(int? Id, string Name, string Content, IFormFile? Attachment)
{
    public static PageInput From(IFormCollection form)
    {
        var (id, name, content) = (form["id"], form["Name"], form["Content"]);

        int? pageId = null;

        if (!StringValues.IsNullOrEmpty(id))
            pageId = Convert.ToInt32(id);

        IFormFile? file = form.Files["Attachment"];

        return new PageInput(pageId, name!, content!, file);
    }
}

record User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

class PageInputValidator : AbstractValidator<PageInput>
{
    public PageInputValidator(string pageName, string homePageName)
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required");
        if (pageName.Equals(homePageName, StringComparison.OrdinalIgnoreCase))
            RuleFor(x => x.Name).Must(name => name.Equals(homePageName)).WithMessage($"You cannot modify home page name. Please keep it {homePageName}");

        RuleFor(x => x.Content).NotEmpty().WithMessage("Content is required");
    }
}


