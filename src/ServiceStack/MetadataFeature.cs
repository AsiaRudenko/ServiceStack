﻿using System;
using System.Collections.Generic;
using System.Web;
using ServiceStack.DataAnnotations;
using ServiceStack.Host.Handlers;
using ServiceStack.Metadata;
using ServiceStack.NativeTypes;
using ServiceStack.Web;

namespace ServiceStack
{
    public class MetadataFeature : IPlugin
    {
        public string PluginLinksTitle { get; set; }
        public Dictionary<string, string> PluginLinks { get; set; }

        public string DebugLinksTitle { get; set; }
        public Dictionary<string, string> DebugLinks { get; set; }

        public Action<IndexOperationsControl> IndexPageFilter { get; set; }
        public Action<OperationControl> DetailPageFilter { get; set; }
        
        public List<Action<AppMetadata, IRequest>> AppMetadataFilters { get; } = new List<Action<AppMetadata, IRequest>>();

        public bool ShowResponseStatusInMetadataPages { get; set; }

        public bool EnableNav
        {
            get => ServiceRoutes.ContainsKey(typeof(MetadataNavService));
            set
            {
                if (!value)
                    ServiceRoutes.Remove(typeof(MetadataNavService));
            }
        }
        
        public Dictionary<Type, string[]> ServiceRoutes { get; set; } = new Dictionary<Type, string[]> {
            { typeof(MetadataAppService), new[]
            {
                "/" + "metadata".Localize() + "/" + "app".Localize(),
            } },
            { typeof(MetadataNavService), new[] {
                "/" + "metadata".Localize() + "/" + "nav".Localize(),
                "/" + "metadata".Localize() + "/" + "nav".Localize() + "/{Name}",
            } },
        };

        public MetadataFeature()
        {
            PluginLinksTitle = "Plugin Links:";
            PluginLinks = new Dictionary<string, string>();

            DebugLinksTitle = "Debug Info:";
            DebugLinks = new Dictionary<string, string> {
                {"operations/metadata", "Operations Metadata"},
            };
        }

        public void Register(IAppHost appHost)
        {
            appHost.CatchAllHandlers.Add(ProcessRequest);

            if (EnableNav)
            {
                ViewUtils.Load(appHost.AppSettings);
            }

            appHost.RegisterServices(ServiceRoutes);
        }

        public virtual IHttpHandler ProcessRequest(string httpMethod, string pathInfo, string filePath)
        {
            var pathParts = pathInfo.TrimStart('/').Split('/');
            if (pathParts.Length == 0) return null;
            return GetHandlerForPathParts(pathParts);
        }

        private IHttpHandler GetHandlerForPathParts(string[] pathParts)
        {
            var pathController = pathParts[0].ToLowerInvariant();
            if (pathParts.Length == 1)
            {
                if (pathController == "metadata")
                    return new IndexMetadataHandler();

                return null;
            }

            var pathAction = pathParts[1].ToLowerInvariant();
#if !NETSTANDARD2_0
            if (pathAction == "wsdl")
            {
                if (pathController == "soap11")
                    return new Soap11WsdlMetadataHandler();
                if (pathController == "soap12")
                    return new Soap12WsdlMetadataHandler();
            }
#endif

            if (pathAction != "metadata") return null;

            switch (pathController)
            {
                case "json":
                    return new JsonMetadataHandler();

                case "xml":
                    return new XmlMetadataHandler();

                case "jsv":
                    return new JsvMetadataHandler();
#if !NETSTANDARD2_0
                case "soap11":
                    return new Soap11MetadataHandler();

                case "soap12":
                    return new Soap12MetadataHandler();
#endif

                case "operations":
                    return new CustomResponseHandler((httpReq, httpRes) =>
                        HostContext.AppHost.HasAccessToMetadata(httpReq, httpRes)
                            ? HostContext.Metadata.GetOperationDtos()
                            : null, "Operations");

                default:
                    if (HostContext.ContentTypes
                        .ContentTypeFormats.TryGetValue(pathController, out var contentType))
                    {
                        var format = ContentFormat.GetContentFormat(contentType);
                        return new CustomMetadataHandler(contentType, format);
                    }
                    break;
            }
            return null;
        }
    }

    [DefaultRequest(typeof(GetNavItems))]
    [Restrict(VisibilityTo = RequestAttributes.None)]
    public class MetadataNavService : Service
    {
        public object Get(GetNavItems request)
        {
            return request.Name != null
                ? new GetNavItemsResponse {
                    BaseUrl = Request.GetBaseUrl(),
                    Results = ViewUtils.NavItemsMap.TryGetValue(request.Name, out var navItems)
                        ? navItems
                        : TypeConstants<NavItem>.EmptyList,
                }
                : new GetNavItemsResponse {
                    BaseUrl = Request.GetBaseUrl(),
                    Results = ViewUtils.NavItems,
                    NavItemsMap = ViewUtils.NavItemsMap,
                };
        }
    }

    [Exclude(Feature.Soap)]
    public class MetadataApp { }

    [DefaultRequest(typeof(MetadataApp))]
    [Restrict(VisibilityTo = RequestAttributes.None)]
    public class MetadataAppService : Service
    {
        public INativeTypesMetadata NativeTypesMetadata { get; set; }
        public AppMetadata Any(MetadataApp request)
        {
            var typesConfig = NativeTypesMetadata.GetConfig(new TypesMetadata());
            var metadataTypes = NativeTypesMetadata.GetMetadataTypes(Request, typesConfig);
            metadataTypes.Config = null;
            
            var appHost = HostContext.AssertAppHost();
            var response = new AppMetadata {
                App = appHost.Config.AppInfo ?? new AppInfo(),
                ContentTypeFormats = appHost.ContentTypes.ContentTypeFormats,
                AuthProviders = Auth.AuthenticateService.GetAuthProviders().Map(x => new MetaAuthProvider {
                    Type = x.GetType().Name,
                    Name = x.Provider,
                    NavItem = (x as Auth.AuthProvider)?.NavItem,
                }),
                Plugins = new PluginInfo(),
                Api = metadataTypes,
            };
            
            if (response.App.BaseUrl == null)
                response.App.BaseUrl = Request.GetBaseUrl();
            if (response.App.ServiceName == null)
                response.App.ServiceName = appHost.ServiceName;

            foreach (var fn in HostContext.AssertPlugin<MetadataFeature>().AppMetadataFilters)
            {
                fn(response, Request);
            }
            
            return response;
        }
    }

    public static class MetadataFeatureExtensions
    {
        public static MetadataFeature AddPluginLink(this MetadataFeature metadata, string href, string title)
        {
            if (metadata != null)
            {
                metadata.PluginLinks[href] = title;
            }
            return metadata;
        }

        public static MetadataFeature RemovePluginLink(this MetadataFeature metadata, string href)
        {
            metadata.PluginLinks.Remove(href);
            return metadata;
        }

        public static MetadataFeature AddDebugLink(this MetadataFeature metadata, string href, string title)
        {
            if (metadata != null)
            {
                metadata.DebugLinks[href] = title;
            }
            return metadata;
        }

        public static MetadataFeature RemoveDebugLink(this MetadataFeature metadata, string href)
        {
            metadata.DebugLinks.Remove(href);
            return metadata;
        }
    }
}