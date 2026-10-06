using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers.Http;

namespace Leaves_LimitedTraders;

// The web config surface. Registering these two routes with Kind "config" is what makes the
// shared /webui/config editor pick this mod up (GET = view, GET + POST = editable).
//
//   GET  /limitedtraders/config  -> { Schema, Current }   (HttpUtils.WriteConfigView)
//   POST /limitedtraders/config  -> { Ok, Messages }      (mod.ApplyConfigJson -> HttpUtils.WriteConfigApply)
[Injectable]
public class LimitedTradersConfigApi(LimitedTraders mod) : IHttpListener, IOnLoad
{
    private const string ConfigPath = "/limitedtraders/config";

    public Task OnLoad()
    {
        var routes = new List<Leaves.RouteInfo>
        {
            new Leaves.RouteInfo("GET", ConfigPath, "Config: current values + schema.", "config"),
            new Leaves.RouteInfo("POST", ConfigPath, "Config: apply edits.", "config", danger: true),
        };
        Leaves.Utils.Instance.RegisterRoutes("Limited Traders", routes);
        return Task.CompletedTask;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        return Leaves.HttpUtils.HasPrefix(context, "/limitedtraders/");
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        if (Leaves.HttpUtils.IsGet(context, ConfigPath))
        {
            await Leaves.HttpUtils.WriteConfigView<LimitedTradersConfig>(context, mod.CurrentConfig);
            return;
        }

        if (Leaves.HttpUtils.IsPost(context, ConfigPath))
        {
            string body = await Leaves.HttpUtils.ReadBody(context);
            Leaves.HttpUtils.ConfigApplyResult result = mod.ApplyConfigJson(body);
            await Leaves.HttpUtils.WriteConfigApply(context, result);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync($"Unknown Limited Traders resource: {context.Request.Path}");
    }
}
