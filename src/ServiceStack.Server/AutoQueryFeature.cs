﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ServiceStack.MiniProfiler;
using ServiceStack.Web;
using ServiceStack.Data;
using ServiceStack.Extensions;
using ServiceStack.Host;
using ServiceStack.OrmLite;
using ServiceStack.Text;

namespace ServiceStack
{
    public delegate void QueryFilterDelegate(ISqlExpression q, IQueryDb dto, IRequest req);

    public class QueryDbFilterContext
    {
        public IDbConnection Db { get; set; }
        public List<Command> Commands { get; set; }
        public IQueryDb Dto { get; set; }
        public ISqlExpression SqlExpression { get; set; }
        public IQueryResponse Response { get; set; }
    }

    public partial class AutoQueryFeature : IPlugin, IPostInitPlugin, Model.IHasStringId
    {
        public string Id { get; set; } = Plugins.AutoQuery;
        private static readonly string[] DefaultIgnoreProperties = 
            {"Skip", "Take", "OrderBy", "OrderByDesc", "Fields", "_select", "_from", "_join", "_where"};
        public HashSet<string> IgnoreProperties { get; set; } = new HashSet<string>(DefaultIgnoreProperties, StringComparer.OrdinalIgnoreCase);
        public HashSet<string> IllegalSqlFragmentTokens { get; set; } = new HashSet<string>(OrmLiteUtils.IllegalSqlFragmentTokens);
        public HashSet<Assembly> LoadFromAssemblies { get; set; } = new HashSet<Assembly>();
        public int? MaxLimit { get; set; }
        public bool IncludeTotal { get; set; }
        public bool StripUpperInLike { get; set; } = OrmLiteConfig.StripUpperInLike;
        public bool EnableUntypedQueries { get; set; } = true;
        public bool EnableRawSqlFilters { get; set; }
        public bool EnableAutoQueryViewer { get; set; } = true;
        public bool EnableAsync { get; set; } = true;
        public bool OrderByPrimaryKeyOnPagedQuery { get; set; } = true;
        public string UseNamedConnection { get; set; }
        public Type AutoQueryServiceBaseType { get; set; } = typeof(AutoQueryServiceBase);
        public QueryFilterDelegate GlobalQueryFilter { get; set; }
        public Dictionary<Type, QueryFilterDelegate> QueryFilters { get; set; } = new Dictionary<Type, QueryFilterDelegate>();
        public List<Action<QueryDbFilterContext>> ResponseFilters { get; set; }
        public Action<Type, TypeBuilder, MethodBuilder, ILGenerator> GenerateServiceFilter { get; set; }

        /// <summary>
        /// Enable code-gen of CRUD Services for registered database in any supported Add ServiceStack Reference Language:
        ///  - /autocrud/{Include}/{Lang}
        /// 
        /// View DB Schema Services:
        ///  - /autocrud/schema - Default DB
        ///  - /autocrud/schema/{Schema} - Specified DB Schema
        /// </summary>
        public IGenerateCrudServices GenerateCrudServices { get; set; }

        public Dictionary<string, string> ImplicitConventions = new Dictionary<string, string> 
        {
            {"%Above%",         SqlTemplate.GreaterThan},
            {"Begin%",          SqlTemplate.GreaterThan},
            {"%Beyond%",        SqlTemplate.GreaterThan},
            {"%Over%",          SqlTemplate.GreaterThan},
            {"%OlderThan",      SqlTemplate.GreaterThan},
            {"%After%",         SqlTemplate.GreaterThan},
            {"OnOrAfter%",      SqlTemplate.GreaterThanOrEqual},
            {"%From%",          SqlTemplate.GreaterThanOrEqual},
            {"Since%",          SqlTemplate.GreaterThanOrEqual},
            {"Start%",          SqlTemplate.GreaterThanOrEqual},
            {"%Higher%",        SqlTemplate.GreaterThanOrEqual},
            {">%",              SqlTemplate.GreaterThanOrEqual},
            {"%>",              SqlTemplate.GreaterThan},
            {"%!",              SqlTemplate.NotEqual},

            {"%GreaterThanOrEqualTo%", SqlTemplate.GreaterThanOrEqual},
            {"%GreaterThan%",          SqlTemplate.GreaterThan},
            {"%LessThan%",             SqlTemplate.LessThan},
            {"%LessThanOrEqualTo%",    SqlTemplate.LessThanOrEqual},
            {"%NotEqualTo",            SqlTemplate.NotEqual},

            {"Behind%",         SqlTemplate.LessThan},
            {"%Below%",         SqlTemplate.LessThan},
            {"%Under%",         SqlTemplate.LessThan},
            {"%Lower%",         SqlTemplate.LessThan},
            {"%Before%",        SqlTemplate.LessThan},
            {"%YoungerThan",    SqlTemplate.LessThan},
            {"OnOrBefore%",     SqlTemplate.LessThanOrEqual},
            {"End%",            SqlTemplate.LessThanOrEqual},
            {"Stop%",           SqlTemplate.LessThanOrEqual},
            {"To%",             SqlTemplate.LessThanOrEqual},
            {"Until%",          SqlTemplate.LessThanOrEqual},
            {"%<",              SqlTemplate.LessThanOrEqual},
            {"<%",              SqlTemplate.LessThan},

            {"%Like%",          SqlTemplate.CaseInsensitiveLike },
            {"%In",             "{Field} IN ({Values})"},
            {"%Ids",            "{Field} IN ({Values})"},
            {"%Between%",       "{Field} BETWEEN {Value1} AND {Value2}"},
        };

        public Dictionary<string, QueryDbFieldAttribute> StartsWithConventions =
            new Dictionary<string, QueryDbFieldAttribute>();

        public Dictionary<string, QueryDbFieldAttribute> EndsWithConventions = new Dictionary<string, QueryDbFieldAttribute>
        {
            { "StartsWith", new QueryDbFieldAttribute { Template = SqlTemplate.CaseInsensitiveLike, ValueFormat = "{0}%" }},
            { "Contains", new QueryDbFieldAttribute { Template = SqlTemplate.CaseInsensitiveLike, ValueFormat = "%{0}%" }},
            { "EndsWith", new QueryDbFieldAttribute { Template = SqlTemplate.CaseInsensitiveLike, ValueFormat = "%{0}" }},
        };

        public List<AutoQueryConvention> ViewerConventions { get; set; } = new List<AutoQueryConvention> 
        {
            new AutoQueryConvention {Name = "=", Value = "%"},
            new AutoQueryConvention {Name = "!=", Value = "%!"},
            new AutoQueryConvention {Name = ">=", Value = ">%"},
            new AutoQueryConvention {Name = ">", Value = "%>"},
            new AutoQueryConvention {Name = "<=", Value = "%<"},
            new AutoQueryConvention {Name = "<", Value = "<%"},
            new AutoQueryConvention {Name = "In", Value = "%In"},
            new AutoQueryConvention {Name = "Between", Value = "%Between"},
            new AutoQueryConvention {Name = "Starts With", Value = "%StartsWith", Types = "string"},
            new AutoQueryConvention {Name = "Contains", Value = "%Contains", Types = "string"},
            new AutoQueryConvention {Name = "Ends With", Value = "%EndsWith", Types = "string"},
        };

        public AutoQueryFeature()
        {
            ResponseFilters = new List<Action<QueryDbFilterContext>> { IncludeAggregates };
        }

        public void Register(IAppHost appHost)
        {
            if (StripUpperInLike)
            {
                if (ImplicitConventions.TryGetValue("%Like%", out var convention) && convention == SqlTemplate.CaseInsensitiveLike)
                    ImplicitConventions["%Like%"] = SqlTemplate.CaseSensitiveLike;

                foreach (var attr in EndsWithConventions)
                {
                    if (attr.Value.Template == SqlTemplate.CaseInsensitiveLike)
                        attr.Value.Template = SqlTemplate.CaseSensitiveLike;
                }
            }

            foreach (var entry in ImplicitConventions)
            {
                var key = entry.Key.Trim('%');
                var fmt = entry.Value;
                var query = new QueryDbFieldAttribute { Template = fmt }.Init();
                if (entry.Key.EndsWith("%"))
                    StartsWithConventions[key] = query;
                if (entry.Key.StartsWith("%"))
                    EndsWithConventions[key] = query;
            }

            var container = appHost.GetContainer();
            container.AddSingleton<IAutoQueryDb>(c => new AutoQuery
                {
                    IgnoreProperties = IgnoreProperties,
                    IllegalSqlFragmentTokens = IllegalSqlFragmentTokens,
                    MaxLimit = MaxLimit,
                    IncludeTotal = IncludeTotal,
                    EnableUntypedQueries = EnableUntypedQueries,
                    EnableSqlFilters = EnableRawSqlFilters,
                    OrderByPrimaryKeyOnLimitQuery = OrderByPrimaryKeyOnPagedQuery,
                    GlobalQueryFilter = GlobalQueryFilter,
                    QueryFilters = QueryFilters,
                    ResponseFilters = ResponseFilters,
                    StartsWithConventions = StartsWithConventions,
                    EndsWithConventions = EndsWithConventions,
                    UseNamedConnection = UseNamedConnection,
                });

            appHost.Metadata.GetOperationAssemblies()
                .Each(x => LoadFromAssemblies.Add(x));

            ((ServiceStackHost)appHost).ServiceAssemblies.Each(x => {
                if (!LoadFromAssemblies.Contains(x))
                    LoadFromAssemblies.Add(x);
            });

            appHost.AddToAppMetadata(meta => {
                meta.Plugins.AutoQuery = new AutoQueryInfo {
                    MaxLimit = MaxLimit,
                    UntypedQueries = EnableUntypedQueries.NullIfFalse(),
                    RawSqlFilters = EnableRawSqlFilters.NullIfFalse(),
                    Async = EnableAsync.NullIfFalse(),
                    AutoQueryViewer = EnableAutoQueryViewer.NullIfFalse(),
                    OrderByPrimaryKey = OrderByPrimaryKeyOnPagedQuery.NullIfFalse(),
                    CrudEvents = container.Exists<ICrudEvents>().NullIfFalse(),
                    CrudEventsServices = (ServiceRoutes.ContainsKey(typeof(GetCrudEventsService)) && AccessRole != null).NullIfFalse(),
                    AccessRole = AccessRole,
                    NamedConnection = UseNamedConnection,
                    ViewerConventions = ViewerConventions,
                };
            });

            if (EnableAutoQueryViewer && appHost.GetPlugin<AutoQueryMetadataFeature>() == null)
                appHost.LoadPlugin(new AutoQueryMetadataFeature { MaxLimit = MaxLimit });
            
            appHost.GetPlugin<MetadataFeature>()?.ExportTypes.Add(typeof(CrudEvent));
            
            //CRUD Services
            GenerateCrudServices?.Register(appHost);

            OnRegister(appHost);
        }

        public void AfterPluginsLoaded(IAppHost appHost)
        {
            var scannedTypes = new HashSet<Type>();
            
            var crudServices = GenerateCrudServices?.GenerateMissingServices(this);
            crudServices?.Each(x => scannedTypes.Add(x));

            foreach (var assembly in LoadFromAssemblies)
            {
                try
                {
                    assembly.GetTypes().Each(x => scannedTypes.Add(x));
                }
                catch (Exception ex)
                {
                    appHost.NotifyStartupException(ex);
                }
            }

            var missingQueryRequestTypes = scannedTypes
                .Where(x => x.HasInterface(typeof(IQueryDb)) &&
                                 !appHost.Metadata.OperationsMap.ContainsKey(x))
                .ToList();
            var missingCrudRequestTypes = scannedTypes
                .Where(x => x.HasInterface(typeof(ICrud)) &&
                            !appHost.Metadata.OperationsMap.ContainsKey(x))
                .ToList();

            if (missingQueryRequestTypes.Count == 0 && missingCrudRequestTypes.Count == 0)
                return;

            var serviceType = GenerateMissingQueryServices(missingQueryRequestTypes, missingCrudRequestTypes);
            appHost.RegisterService(serviceType);
        }

        Type GenerateMissingQueryServices(
            List<Type> missingQueryRequestTypes, List<Type> missingCrudRequestTypes)
        {
            var assemblyName = new AssemblyName { Name = "tmpAssembly" };
            var typeBuilder =
                AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run)
                .DefineDynamicModule("tmpModule")
                .DefineType("__AutoQueryServices",
                    TypeAttributes.Public | TypeAttributes.Class,
                    AutoQueryServiceBaseType);

            foreach (var requestType in missingQueryRequestTypes)
            {
                if (requestType.IsAbstract || requestType.IsGenericType)
                    continue;
                
                var genericDef = requestType.GetTypeWithGenericTypeDefinitionOf(typeof(IQueryDb<,>));
                var hasExplicitInto = genericDef != null;
                if (genericDef == null)
                    genericDef = requestType.GetTypeWithGenericTypeDefinitionOf(typeof(IQueryDb<>));
                if (genericDef == null)
                    continue;

                var method = typeBuilder.DefineMethod(ActionContext.AnyMethod, MethodAttributes.Public | MethodAttributes.Virtual,
                    CallingConventions.Standard,
                    returnType: typeof(object),
                    parameterTypes: new[] { requestType });

                var il = method.GetILGenerator();

                GenerateServiceFilter?.Invoke(requestType, typeBuilder, method, il);

                var queryMethod = EnableAsync
                    ? nameof(AutoQueryServiceBase.ExecAsync)
                    : nameof(AutoQueryServiceBase.Exec);
                
                var genericArgs = genericDef.GetGenericArguments();
                var mi = AutoQueryServiceBaseType.GetMethods()
                    .First(x => x.Name == queryMethod && 
                                x.GetGenericArguments().Length == genericArgs.Length);
                var genericMi = mi.MakeGenericMethod(genericArgs);

                var queryType = hasExplicitInto
                    ? typeof(IQueryDb<,>).MakeGenericType(genericArgs)
                    : typeof(IQueryDb<>).MakeGenericType(genericArgs);

                il.Emit(OpCodes.Nop);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Box, queryType);
                il.Emit(OpCodes.Callvirt, genericMi);
                il.Emit(OpCodes.Ret);
            }
            
            foreach (var requestType in missingCrudRequestTypes)
            {
                if (requestType.IsAbstract || requestType.IsGenericType)
                    continue;
                
                var crudTypes = AutoCrudOperation.GetAutoCrudDtoType(requestType);
                
                if (crudTypes == null)
                    continue;

                var genericDef = crudTypes.Value.GenericDef;
                var crudType = crudTypes.Value.ModelType;
                var methodName = crudType.Name.LeftPart('`').Substring(1);
                methodName = methodName.Substring(0, methodName.Length - 2);
                
                if (!requestType.HasInterface(typeof(IReturnVoid)) &&
                    !requestType.IsOrHasGenericInterfaceTypeOf(typeof(IReturn<>)))
                    throw new NotSupportedException($"'{requestType.Name}' I{methodName}Db<T> AutoQuery Service must implement IReturn<T> or IReturnVoid");
                
                var method = typeBuilder.DefineMethod(ActionContext.AnyMethod, MethodAttributes.Public | MethodAttributes.Virtual,
                    CallingConventions.Standard,
                    returnType: typeof(object),
                    parameterTypes: new[] { requestType });

                var il = method.GetILGenerator();

                GenerateServiceFilter?.Invoke(requestType, typeBuilder, method, il);

                var crudMethod = EnableAsync
                    ? methodName + "Async"
                    : methodName;
                
                var genericArgs = genericDef.GetGenericArguments();
                var mi = AutoQueryServiceBaseType.GetMethods()
                    .First(x => x.Name == crudMethod && 
                           x.GetGenericArguments().Length == genericArgs.Length);
                var genericMi = mi.MakeGenericMethod(genericArgs);

                var crudTypeArg = crudType.MakeGenericType(genericArgs);

                il.Emit(OpCodes.Nop);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Box, crudTypeArg);
                il.Emit(OpCodes.Callvirt, genericMi);
                il.Emit(OpCodes.Ret);
            }

            var servicesType = typeBuilder.CreateTypeInfo().AsType();
            return servicesType;
        }

        public AutoQueryFeature RegisterQueryFilter<Request, From>(Action<SqlExpression<From>, Request, IRequest> filterFn)
        {
            QueryFilters[typeof(Request)] = (q, dto, req) =>
                filterFn((SqlExpression<From>)q, (Request)dto, req);

            return this;
        }

        public HashSet<string> SqlAggregateFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AVG", "COUNT", "FIRST", "LAST", "MAX", "MIN", "SUM"
        };

        public void IncludeAggregates(QueryDbFilterContext ctx)
        {
            var commands = ctx.Commands;
            if (commands.Count == 0)
                return;

            var q = ctx.SqlExpression.GetUntypedSqlExpression()
                .Clone()
                .ClearLimits()
                .OrderBy();

            var aggregateCommands = new List<Command>();
            foreach (var cmd in commands)
            {
                if (!SqlAggregateFunctions.Contains(cmd.Name))
                    continue;

                aggregateCommands.Add(cmd);

                if (cmd.Args.Count == 0)
                    cmd.Args.Add("*".AsMemory());

                cmd.Original = cmd.AsMemory();

                var hasAlias = !cmd.Suffix.IsNullOrWhiteSpace();

                for (var i = 0; i < cmd.Args.Count; i++)
                {
                    var arg = cmd.Args[i];

                    string modifier = "";
                    if (arg.StartsWith("DISTINCT ", StringComparison.OrdinalIgnoreCase))
                    {
                        arg.SplitOnFirst(' ', out var first, out var last);
                        modifier = first + " ";
                        arg = last;
                    }

                    var fieldRef = q.FirstMatchingField(arg.ToString());
                    if (fieldRef != null)
                    {
                        //To return predictable aliases, if it's primary table don't fully qualify name
                        var fieldName = fieldRef.Item2.FieldName;
                        var needsRewrite = !fieldName.EqualsIgnoreCase(q.DialectProvider.NamingStrategy.GetColumnName(fieldName)); 
                        if (fieldRef.Item1 != q.ModelDef || fieldRef.Item2.Alias != null || needsRewrite || hasAlias)
                        {
                            cmd.Args[i] = (modifier + q.DialectProvider.GetQuotedColumnName(fieldRef.Item1, fieldRef.Item2)).AsMemory();
                        }
                    }
                    else
                    {
                        double d;
                        if (!arg.EqualsOrdinal("*") && !double.TryParse(arg.ToString(), out d))
                        {
                            cmd.Args[i] = "{0}".SqlFmt(arg).AsMemory();
                        }
                    }
                }

                if (hasAlias)
                {
                    var alias = cmd.Suffix.TrimStart().ToString();
                    if (alias.StartsWith("as ", StringComparison.OrdinalIgnoreCase))
                        alias = alias.Substring("as ".Length);

                    cmd.Suffix = (" " + alias.SafeVarName()).AsMemory();
                }
                else
                {
                    cmd.Suffix = (" " + q.DialectProvider.GetQuotedName(cmd.Original.ToString())).AsMemory();
                }
            }

            var selectSql = string.Join(", ", aggregateCommands.Map(x => x.ToString()));
            q.UnsafeSelect(selectSql);

            var rows = ctx.Db.Select<Dictionary<string, object>>(q);
            var row = rows.FirstOrDefault();

            foreach (var key in row.Keys)
            {
                ctx.Response.Meta[key] = row[key]?.ToString();
            }

            ctx.Commands.RemoveAll(aggregateCommands.Contains);
        }
    }

    public interface IAutoQueryDb : IAutoCrudDb
    {
        Type GetFromType(Type requestDtoType);
        IDbConnection GetDb(Type fromType, IRequest req = null);
        IDbConnection GetDb<From>(IRequest req = null);
        
        ITypedQuery GetTypedQuery(Type dtoType, Type fromType);

        SqlExpression<From> CreateQuery<From>(IQueryDb<From> dto, Dictionary<string, string> dynamicParams, IRequest req = null, IDbConnection db = null);

        QueryResponse<From> Execute<From>(IQueryDb<From> model, SqlExpression<From> query, IRequest req = null, IDbConnection db = null);

        Task<QueryResponse<From>> ExecuteAsync<From>(IQueryDb<From> model, SqlExpression<From> query, IRequest req = null, IDbConnection db = null);

        SqlExpression<From> CreateQuery<From, Into>(IQueryDb<From, Into> dto, Dictionary<string, string> dynamicParams, IRequest req = null, IDbConnection db = null);

        QueryResponse<Into> Execute<From, Into>(IQueryDb<From, Into> model, SqlExpression<From> query, IRequest req = null, IDbConnection db = null);

        Task<QueryResponse<Into>> ExecuteAsync<From, Into>(IQueryDb<From, Into> model, SqlExpression<From> query, IRequest req = null, IDbConnection db = null);
        
        ISqlExpression CreateQuery(IQueryDb dto, Dictionary<string, string> dynamicParams, IRequest req, IDbConnection db);

        /// <summary>
        /// Execute an AutoQuery Request 
        /// </summary>
        IQueryResponse Execute(IQueryDb request, ISqlExpression q, IDbConnection db);

        /// <summary>
        /// Execute an AutoQuery Request 
        /// </summary>
        Task<IQueryResponse> ExecuteAsync(IQueryDb request, ISqlExpression q, IDbConnection db);
    }

    public interface IAutoCrudDb
    {
        /// <summary>
        /// Inserts new entry into Table
        /// </summary>
        object Create<Table>(ICreateDb<Table> dto, IRequest req);
        
        /// <summary>
        /// Inserts new entry into Table Async
        /// </summary>
        Task<object> CreateAsync<Table>(ICreateDb<Table> dto, IRequest req);
        
        /// <summary>
        /// Updates entry into Table
        /// </summary>
        object Update<Table>(IUpdateDb<Table> dto, IRequest req);
        
        /// <summary>
        /// Updates entry into Table Async
        /// </summary>
        Task<object> UpdateAsync<Table>(IUpdateDb<Table> dto, IRequest req);
        
        /// <summary>
        /// Partially Updates entry into Table (Uses OrmLite UpdateNonDefaults behavior)
        /// </summary>
        object Patch<Table>(IPatchDb<Table> dto, IRequest req);
        
        /// <summary>
        /// Partially Updates entry into Table Async (Uses OrmLite UpdateNonDefaults behavior)
        /// </summary>
        Task<object> PatchAsync<Table>(IPatchDb<Table> dto, IRequest req);
        
        /// <summary>
        /// Deletes entry from Table
        /// </summary>
        object Delete<Table>(IDeleteDb<Table> dto, IRequest req);
        
        /// <summary>
        /// Deletes entry from Table Async
        /// </summary>
        Task<object> DeleteAsync<Table>(IDeleteDb<Table> dto, IRequest req);

        /// <summary>
        /// Inserts or Updates entry into Table
        /// </summary>
        object Save<Table>(ISaveDb<Table> dto, IRequest req);

        /// <summary>
        /// Inserts or Updates entry into Table Async
        /// </summary>
        Task<object> SaveAsync<Table>(ISaveDb<Table> dto, IRequest req);
    }
    
    public abstract partial class AutoQueryServiceBase : Service
    {
        public IAutoQueryDb AutoQuery { get; set; }

        public virtual object Exec<From>(IQueryDb<From> dto)
        {
            SqlExpression<From> q;
            using var db = AutoQuery.GetDb<From>(Request);
            using (Profiler.Current.Step("AutoQuery.CreateQuery"))
            {
                var reqParams = Request.IsInProcessRequest()
                    ? new Dictionary<string, string>()
                    : Request.GetRequestParams();
                q = AutoQuery.CreateQuery(dto, reqParams, Request, db);
            }
            using (Profiler.Current.Step("AutoQuery.Execute"))
            {
                return AutoQuery.Execute(dto, q, db);
            }
        }

        public virtual async Task<object> ExecAsync<From>(IQueryDb<From> dto)
        {
            SqlExpression<From> q;
            using var db = AutoQuery.GetDb<From>(Request);
            using (Profiler.Current.Step("AutoQuery.CreateQuery"))
            {
                var reqParams = Request.IsInProcessRequest()
                    ? new Dictionary<string, string>()
                    : Request.GetRequestParams();
                q = AutoQuery.CreateQuery(dto, reqParams, Request, db);
            }
            using (Profiler.Current.Step("AutoQuery.Execute"))
            {
                return await AutoQuery.ExecuteAsync(dto, q, db);
            }
        }

        public virtual object Exec<From, Into>(IQueryDb<From, Into> dto)
        {
            SqlExpression<From> q;
            using var db = AutoQuery.GetDb<From>(Request);
            using (Profiler.Current.Step("AutoQuery.CreateQuery"))
            {
                var reqParams = Request.IsInProcessRequest()
                    ? new Dictionary<string, string>()
                    : Request.GetRequestParams();
                q = AutoQuery.CreateQuery(dto, reqParams, Request, db);
            }
            using (Profiler.Current.Step("AutoQuery.Execute"))
            {
                return AutoQuery.Execute(dto, q, db);
            }
        }

        public virtual async Task<object> ExecAsync<From, Into>(IQueryDb<From, Into> dto)
        {
            SqlExpression<From> q;
            using var db = AutoQuery.GetDb<From>(Request);
            using (Profiler.Current.Step("AutoQuery.CreateQuery"))
            {
                var reqParams = Request.IsInProcessRequest()
                    ? new Dictionary<string, string>()
                    : Request.GetRequestParams();
                q = AutoQuery.CreateQuery(dto, reqParams, Request, db);
            }
            using (Profiler.Current.Step("AutoQuery.Execute"))
            {
                return await AutoQuery.ExecuteAsync(dto, q, db);
            }
        }
    }

    public interface IAutoQueryOptions
    {
        int? MaxLimit { get; set; }
        bool IncludeTotal { get; set; }
        bool EnableUntypedQueries { get; set; }
        bool EnableSqlFilters { get; set; }
        bool OrderByPrimaryKeyOnLimitQuery { get; set; }
        HashSet<string> IgnoreProperties { get; set; }
        HashSet<string> IllegalSqlFragmentTokens { get; set; }
        Dictionary<string, QueryDbFieldAttribute> StartsWithConventions { get; set; }
        Dictionary<string, QueryDbFieldAttribute> EndsWithConventions { get; set; }
    }

    public partial class AutoQuery : IAutoQueryDb, IAutoQueryOptions
    {
        public int? MaxLimit { get; set; }
        public bool IncludeTotal { get; set; }
        public bool EnableUntypedQueries { get; set; }
        public bool EnableSqlFilters { get; set; }
        public bool OrderByPrimaryKeyOnLimitQuery { get; set; }
        public string RequiredRoleForRawSqlFilters { get; set; }
        public HashSet<string> IgnoreProperties { get; set; }
        public HashSet<string> IllegalSqlFragmentTokens { get; set; }
        public Dictionary<string, QueryDbFieldAttribute> StartsWithConventions { get; set; }
        public Dictionary<string, QueryDbFieldAttribute> EndsWithConventions { get; set; }

        public string UseNamedConnection { get; set; }
        public QueryFilterDelegate GlobalQueryFilter { get; set; }
        public Dictionary<Type, QueryFilterDelegate> QueryFilters { get; set; }
        public List<Action<QueryDbFilterContext>> ResponseFilters { get; set; }

        private static Dictionary<Type, ITypedQuery> TypedQueries = new Dictionary<Type, ITypedQuery>();

        public Type GetFromType(Type requestDtoType)
        {
            var intoTypeDef = requestDtoType.GetTypeWithGenericTypeDefinitionOf(typeof(IQueryDb<,>));
            if (intoTypeDef != null)
            {
                var args = intoTypeDef.GetGenericArguments();
                return args[1];
            }
            
            var typeDef = requestDtoType.GetTypeWithGenericTypeDefinitionOf(typeof(IQueryDb<>));
            if (typeDef != null)
            {
                var args = typeDef.GetGenericArguments();
                return args[0];
            }

            throw new NotSupportedException("Request DTO is not an AutoQuery DTO: " + requestDtoType.Name);
        }

        public ITypedQuery GetTypedQuery(Type dtoType, Type fromType)
        {
            if (TypedQueries.TryGetValue(dtoType, out var defaultValue)) 
                return defaultValue;

            var genericType = typeof(TypedQuery<,>).MakeGenericType(dtoType, fromType);
            defaultValue = genericType.CreateInstance<ITypedQuery>();

            Dictionary<Type, ITypedQuery> snapshot, newCache;
            do
            {
                snapshot = TypedQueries;
                newCache = new Dictionary<Type, ITypedQuery>(TypedQueries) {
                    [dtoType] = defaultValue
                };

            } while (!ReferenceEquals(
                Interlocked.CompareExchange(ref TypedQueries, newCache, snapshot), snapshot));

            return defaultValue;
        }

        public SqlExpression<From> Filter<From>(ISqlExpression q, IQueryDb dto, IRequest req)
        {
            GlobalQueryFilter?.Invoke(q, dto, req);

            if (QueryFilters == null)
                return (SqlExpression<From>)q;

            if (!QueryFilters.TryGetValue(dto.GetType(), out var filterFn))
            {
                foreach (var type in dto.GetType().GetInterfaces())
                {
                    if (QueryFilters.TryGetValue(type, out filterFn))
                        break;
                }
            }

            filterFn?.Invoke(q, dto, req);

            return (SqlExpression<From>)q;
        }

        public ISqlExpression Filter(ISqlExpression q, IQueryDb dto, IRequest req)
        {
            GlobalQueryFilter?.Invoke(q, dto, req);

            if (QueryFilters == null)
                return q;

            if (!QueryFilters.TryGetValue(dto.GetType(), out var filterFn))
            {
                foreach (var type in dto.GetType().GetInterfaces())
                {
                    if (QueryFilters.TryGetValue(type, out filterFn))
                        break;
                }
            }

            filterFn?.Invoke(q, dto, req);

            return q;
        }

        public QueryResponse<Into> ResponseFilter<From, Into>(IDbConnection db, QueryResponse<Into> response, SqlExpression<From> expr, IQueryDb dto)
        {
            response.Meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var commands = dto.Include.ParseCommands();

            var ctx = new QueryDbFilterContext
            {
                Db = db,
                Commands = commands,
                Dto = dto,
                SqlExpression = expr,
                Response = response,
            };

            var totalCommand = commands.FirstOrDefault(x => x.Name.EqualsIgnoreCase("Total"));
            if (totalCommand != null)
            {
                totalCommand.Name = "COUNT";
            }

            var totalRequested = commands.Any(x =>
                x.Name.EqualsIgnoreCase("COUNT") &&
                (x.Args.Count == 0 || x.Args.Count == 1 && x.Args[0].EqualsOrdinal("*")));

            if (IncludeTotal || totalRequested)
            {
                if (!totalRequested)
                    commands.Add(new Command { Name = "COUNT", Args = { "*".AsMemory() } });

                foreach (var responseFilter in ResponseFilters)
                {
                    responseFilter(ctx);
                }

                response.Total = response.Meta.TryGetValue("COUNT(*)", out var total)
                    ? total.ToInt()
                    : (int)db.Count(expr); //fallback if it's not populated (i.e. if stripped by custom ResponseFilter)

                //reduce payload on wire
                if (totalCommand != null || !totalRequested)
                {
                    response.Meta.Remove("COUNT(*)");
                    if (response.Meta.Count == 0)
                        response.Meta = null;
                }
            }
            else
            {
                foreach (var responseFilter in ResponseFilters)
                {
                    responseFilter(ctx);
                }
            }

            return response;
        }

        public IDbConnection GetDb<From>(IRequest req = null) => GetDb(typeof(From), req);
        public IDbConnection GetDb(Type fromType, IRequest req = null)
        {
            var namedConnection = UseNamedConnection;
            var attr = fromType.FirstAttribute<NamedConnectionAttribute>();
            if (attr != null)
                namedConnection = attr.Name;

            return namedConnection == null 
                ? HostContext.AppHost.GetDbConnection(req)
                : HostContext.TryResolve<IDbConnectionFactory>().OpenDbConnection(namedConnection);
        }

        public SqlExpression<From> CreateQuery<From>(IQueryDb<From> dto, Dictionary<string, string> dynamicParams, IRequest req = null, IDbConnection db = null)
        {
            using (db == null ? db = GetDb<From>(req) : null)
            {
                var typedQuery = GetTypedQuery(dto.GetType(), typeof(From));
                var q = typedQuery.CreateQuery(db);
                return Filter<From>(typedQuery.AddToQuery(q, dto, dynamicParams, this, req), dto, req);
            }
        }

        public QueryResponse<From> Execute<From>(IQueryDb<From> model, SqlExpression<From> query, IRequest req = null, IDbConnection db = null)
        {
            using (db == null ? db = GetDb<From>(req) : null)
            {
                var typedQuery = GetTypedQuery(model.GetType(), typeof(From));
                return ResponseFilter(db, typedQuery.Execute<From>(db, query), query, model);
            }
        }

        public async Task<QueryResponse<From>> ExecuteAsync<From>(IQueryDb<From> model, SqlExpression<From> query, IRequest req = null, IDbConnection db = null)
        {
            using (db == null ? db = GetDb<From>(req) : null)
            {
                var typedQuery = GetTypedQuery(model.GetType(), typeof(From));
                return ResponseFilter(db, await typedQuery.ExecuteAsync<From>(db, query), query, model);
            }
        }

        public SqlExpression<From> CreateQuery<From, Into>(IQueryDb<From, Into> dto, Dictionary<string, string> dynamicParams, IRequest req = null, IDbConnection db = null)
        {
            using (db == null ? db = GetDb<From>(req) : null)
            {
                var typedQuery = GetTypedQuery(dto.GetType(), typeof(From));
                var q = typedQuery.CreateQuery(db);
                return Filter<From>(typedQuery.AddToQuery(q, dto, dynamicParams, this, req), dto, req);
            }
        }

        public QueryResponse<Into> Execute<From, Into>(IQueryDb<From, Into> model, SqlExpression<From> query, IRequest req = null, IDbConnection db = null)
        {
            using (db == null ? db = GetDb<From>(req) : null)
            {
                var typedQuery = GetTypedQuery(model.GetType(), typeof(From));
                return ResponseFilter(db, typedQuery.Execute<Into>(db, query), query, model);
            }
        }

        public async Task<QueryResponse<Into>> ExecuteAsync<From, Into>(IQueryDb<From, Into> model, SqlExpression<From> query, IRequest req = null, IDbConnection db = null)
        {
            using (db == null ? db = GetDb<From>(req) : null)
            {
                var typedQuery = GetTypedQuery(model.GetType(), typeof(From));
                return ResponseFilter(db, await typedQuery.ExecuteAsync<Into>(db, query), query, model);
            }
        }

        public ISqlExpression CreateQuery(IQueryDb requestDto, Dictionary<string, string> dynamicParams, IRequest req = null, IDbConnection db = null)
        {
            var requestDtoType = requestDto.GetType();
            var fromType = GetFromType(requestDtoType);
            using (db == null ? db = GetDb(fromType) : null)
            {
                var typedQuery = GetTypedQuery(requestDtoType, fromType);
                var q = typedQuery.CreateQuery(db);
                return Filter(typedQuery.AddToQuery(q, requestDto, dynamicParams, this, req), requestDto, req);
            }
        }
        
        private Dictionary<Type, GenericAutoQueryDb> genericAutoQueryCache = new Dictionary<Type, GenericAutoQueryDb>();

        public IQueryResponse Execute(IQueryDb request, ISqlExpression q, IDbConnection db)
        {
            if (db == null)
                throw new ArgumentNullException(nameof(db));
            
            var requestDtoType = request.GetType();
            
            ResolveTypes(requestDtoType, out var fromType, out var intoType);

            if (genericAutoQueryCache.TryGetValue(fromType, out GenericAutoQueryDb typedApi))
                return typedApi.ExecuteObject(this, request, q, db);

            var instance = GetGenericAutoQueryDb(fromType, intoType, requestDtoType);

            return instance.ExecuteObject(this, request, q, db);
        }

        public Task<IQueryResponse> ExecuteAsync(IQueryDb request, ISqlExpression q, IDbConnection db)
        {
            if (db == null)
                throw new ArgumentNullException(nameof(db));
            
            var requestDtoType = request.GetType();
            
            ResolveTypes(requestDtoType, out var fromType, out var intoType);

            if (genericAutoQueryCache.TryGetValue(fromType, out GenericAutoQueryDb typedApi))
                return typedApi.ExecuteObjectAsync(this, request, q, db);

            var instance = GetGenericAutoQueryDb(fromType, intoType, requestDtoType);

            return instance.ExecuteObjectAsync(this, request, q, db);
        }

        private GenericAutoQueryDb GetGenericAutoQueryDb(Type fromType, Type intoType, Type requestDtoType)
        {
            var genericType = typeof(GenericAutoQueryDb<,>).MakeGenericType(fromType, intoType);
            var instance = genericType.CreateInstance<GenericAutoQueryDb>();

            Dictionary<Type, GenericAutoQueryDb> snapshot, newCache;
            do
            {
                snapshot = genericAutoQueryCache;
                newCache = new Dictionary<Type, GenericAutoQueryDb>(genericAutoQueryCache) {
                    [requestDtoType] = instance
                };
            } while (!ReferenceEquals(
                Interlocked.CompareExchange(ref genericAutoQueryCache, newCache, snapshot), snapshot));

            return instance;
        }

        private static void ResolveTypes(Type requestDtoType, out Type fromType, out Type intoType)
        {
            var intoTypeDef = requestDtoType.GetTypeWithGenericTypeDefinitionOf(typeof(IQueryDb<,>));
            if (intoTypeDef != null)
            {
                var args = intoTypeDef.GetGenericArguments();
                fromType = args[0];
                intoType = args[1];
            }
            else
            {
                var typeDef = requestDtoType.GetTypeWithGenericTypeDefinitionOf(typeof(IQueryDb<>));
                var args = typeDef.GetGenericArguments();
                fromType = args[0];
                intoType = args[0];
            }
        }
    }

    internal abstract class GenericAutoQueryDb
    {
        public abstract IQueryResponse ExecuteObject(AutoQuery autoQuery, IQueryDb request, ISqlExpression query, IDbConnection db = null);

        public abstract Task<IQueryResponse> ExecuteObjectAsync(AutoQuery autoQuery, IQueryDb request, ISqlExpression query, IDbConnection db = null);
    }
    
    internal class GenericAutoQueryDb<From, Into> : GenericAutoQueryDb
    {
        public override IQueryResponse ExecuteObject(AutoQuery autoQuery, IQueryDb request, ISqlExpression query, IDbConnection db = null)
        {
            using (db == null ? autoQuery.GetDb(request.GetType(), null) : null)
            {
                var typedQuery = autoQuery.GetTypedQuery(request.GetType(), typeof(From));
                var q = (SqlExpression<From>)query;
                return autoQuery.ResponseFilter(db, typedQuery.Execute<Into>(db, q), q, request);
            }
        }

        public override async Task<IQueryResponse> ExecuteObjectAsync(AutoQuery autoQuery, IQueryDb request, ISqlExpression query, IDbConnection db = null)
        {
            using (db == null ? autoQuery.GetDb(request.GetType(), null) : null)
            {
                var typedQuery = autoQuery.GetTypedQuery(request.GetType(), typeof(From));
                var q = (SqlExpression<From>)query;
                return autoQuery.ResponseFilter(db, await typedQuery.ExecuteAsync<Into>(db, q), q, request);
            }
        }
    }

    public interface ITypedQuery
    {
        ISqlExpression CreateQuery(IDbConnection db);

        ISqlExpression AddToQuery(
            ISqlExpression query,
            IQueryDb dto,
            Dictionary<string, string> dynamicParams,
            IAutoQueryOptions options = null,
            IRequest req = null);

        QueryResponse<Into> Execute<Into>(
            IDbConnection db,
            ISqlExpression query);

        Task<QueryResponse<Into>> ExecuteAsync<Into>(
            IDbConnection db,
            ISqlExpression query);
    }

    internal struct ExprResult
    {
        internal string DefaultTerm;
        internal string Format;
        internal object[] Values;
        private ExprResult(string defaultTerm, string format, params object[] values)
        {
            DefaultTerm = defaultTerm;
            Format = format;
            Values = values;
        }
        
        internal static ExprResult? CreateExpression(string defaultTerm, string quotedColumn, object value, QueryDbFieldAttribute implicitQuery)
        {
            var seq = value as IEnumerable;
            if (value is string)
                seq = null;

            if (seq != null && value is ICollection collection && collection.Count == 0)
                return null;

            var format = seq == null
                ? (value != null ? quotedColumn + " = {0}" : quotedColumn + " IS NULL")
                : quotedColumn + " IN ({0})";
            
            if (implicitQuery != null)
            {
                var operand = implicitQuery.Operand ?? "=";
                if (implicitQuery.Term == QueryTerm.Or)
                    defaultTerm = "OR";
                else if (implicitQuery.Term == QueryTerm.And)
                    defaultTerm = "AND";

                format = "(" + quotedColumn + " " + operand + " {0}" + ")";
                if (implicitQuery.Template != null)
                {
                    format = implicitQuery.Template.Replace("{Field}", quotedColumn);

                    if (implicitQuery.ValueStyle == ValueStyle.Multiple)
                    {
                        if (value == null)
                            return null;
                        if (seq == null)
                            throw new ArgumentException($"{implicitQuery.Field} requires {implicitQuery.ValueArity} values");

                        var args = new object[implicitQuery.ValueArity];
                        int i = 0;
                        foreach (var x in seq)
                        {
                            if (i < args.Length)
                            {
                                format = format.Replace("{Value" + (i + 1) + "}", "{" + i + "}");
                                var arg = x;
                                if (implicitQuery.ValueFormat != null)
                                    arg = string.Format(implicitQuery.ValueFormat, arg);
                                args[i++] = arg;
                            }
                        }

                        return new ExprResult(defaultTerm, format, args);
                    }

                    if (implicitQuery.ValueStyle == ValueStyle.List)
                    {
                        if (value == null)
                            return null;
                        if (seq == null)
                            throw new ArgumentException("{0} expects a list of values".Fmt(implicitQuery.Field));

                        format = format.Replace("{Values}", "{0}");
                        value = new SqlInValues(seq);
                    }
                    else
                    {
                        format = format.Replace("{Value}", "{0}");
                    }

                    if (implicitQuery.ValueFormat != null)
                    {
                        value = string.Format(implicitQuery.ValueFormat, value);
                    }
                }
            }
            else
            {
                if (seq != null)
                    value = new SqlInValues(seq);
            }

            return new ExprResult(defaultTerm, format, value);
        }

        internal static QueryDbFieldAttribute ToDbFieldAttribute(AutoFilterAttribute filter)
        {
            var dbField = new QueryDbFieldAttribute {
                Term = filter.Term,
                Field = filter.Field,
                Operand = filter.Operand,
                Template = filter.Template,
                ValueFormat = filter.ValueFormat,
                ValueStyle = ValueStyle.Single,
            };
            if (filter.Template?.IndexOf("{Values}", StringComparison.Ordinal) >= 0)
            {
                dbField.ValueStyle = ValueStyle.List;
            }
            else if (filter.Template?.IndexOf("{Value1}", StringComparison.Ordinal) >= 0)
            {
                dbField.ValueStyle = ValueStyle.Multiple;
                var arity = 1;
                while (filter.Template.IndexOf("{Value" + arity + "}", StringComparison.Ordinal) >= 0)
                {
                    arity++;
                }
                dbField.ValueArity = arity;
            }
            return dbField;
        }
    }

    public class TypedQuery<QueryModel, From> : ITypedQuery
    {
        static readonly Dictionary<string, GetMemberDelegate> PropertyGetters =
            new Dictionary<string, GetMemberDelegate>();

        static readonly Dictionary<string, QueryDbFieldAttribute> QueryFieldMap =
            new Dictionary<string, QueryDbFieldAttribute>();

        static readonly AutoFilterAttribute[] AutoFilters;
        static readonly QueryDbFieldAttribute[] AutoFiltersDbFields;

        static TypedQuery()
        {
            foreach (var pi in typeof(QueryModel).GetPublicProperties())
            {
                var fn = pi.CreateGetter();
                PropertyGetters[pi.Name] = fn;

                var queryAttr = pi.FirstAttribute<QueryDbFieldAttribute>();
                if (queryAttr != null)
                    QueryFieldMap[pi.Name] = queryAttr.Init();
            }

            var allAttrs = typeof(QueryModel).AllAttributes();
            AutoFilters = allAttrs.OfType<AutoFilterAttribute>().ToArray();
            AutoFiltersDbFields = new QueryDbFieldAttribute[AutoFilters.Length];

            for (int i = 0; i < AutoFilters.Length; i++)
            {
                var filter = AutoFilters[i];
                AutoFiltersDbFields[i] = ExprResult.ToDbFieldAttribute(filter);
            }
        }

        public ISqlExpression CreateQuery(IDbConnection db) => db.From<From>();

        public ISqlExpression AddToQuery(
            ISqlExpression query,
            IQueryDb dto, 
            Dictionary<string, string> dynamicParams,
            IAutoQueryOptions options = null,
            IRequest req = null)
        {
            dynamicParams = new Dictionary<string, string>(dynamicParams, StringComparer.OrdinalIgnoreCase);

            var q = (SqlExpression<From>) query;
            if (options != null && options.EnableSqlFilters)
            {
                AppendSqlFilters(q, dto, dynamicParams, options);
            }

            AppendJoins(q, dto);

            AppendLimits(q, dto, options);

            var dtoAttr = dto.GetType().FirstAttribute<QueryDbAttribute>();
            var defaultTerm = dtoAttr != null && dtoAttr.DefaultTerm == QueryTerm.Or ? "OR" : "AND";

            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var props = typeof(From).GetProperties();
            foreach (var pi in props)
            {
                var attr = pi.FirstAttribute<DataMemberAttribute>();
                if (attr?.Name == null) continue;
                aliases[attr.Name] = pi.Name;
            }

            AppendAutoFilters(q, dto, options, req);

            AppendTypedQueries(q, dto, dynamicParams, defaultTerm, options, aliases);

            if (options?.EnableUntypedQueries == true && dynamicParams.Count > 0)
            {
                AppendUntypedQueries(q, dynamicParams, defaultTerm, options, aliases);
            }

            if (defaultTerm == "OR" && q.WhereExpression == null)
            {
                q.Where("1=0"); //Empty OR queries should be empty
            }

            if (!string.IsNullOrEmpty(dto.Fields))
            {
                var fields = dto.Fields;
                var selectDistinct = fields.StartsWith("DISTINCT ", StringComparison.OrdinalIgnoreCase);
                if (selectDistinct)
                {
                    fields = fields.Substring("DISTINCT ".Length);
                }

                var fieldNames = fields.Split(',')
                    .Where(x => x.Trim().Length > 0)
                    .Map(x => x.Trim());

                if (selectDistinct)
                    q.SelectDistinct(fieldNames.ToArray());
                else
                    q.Select(fieldNames.ToArray());
            }

            return q;
        }

        private void AppendSqlFilters(SqlExpression<From> q, IQueryDb dto, Dictionary<string, string> dynamicParams, IAutoQueryOptions options)
        {
            dynamicParams.TryGetValue("_select", out var select);
            if (select != null)
            {
                dynamicParams.Remove("_select");
                select.SqlVerifyFragment(options.IllegalSqlFragmentTokens);
                q.Select(select);
            }

            dynamicParams.TryGetValue("_from", out var from);
            if (from != null)
            {
                dynamicParams.Remove("_from");
                from.SqlVerifyFragment(options.IllegalSqlFragmentTokens);
                q.From(from);
            }

            dynamicParams.TryGetValue("_where", out var where);
            if (where != null)
            {
                dynamicParams.Remove("_where");
                where.SqlVerifyFragment(options.IllegalSqlFragmentTokens);
                q.Where(where);
            }
        }

        private static readonly char[] FieldSeperators = new[] {',', ';'};

        private static void AppendLimits(SqlExpression<From> q, IQueryDb dto, IAutoQueryOptions options)
        {
            var maxLimit = options?.MaxLimit;
            var take = dto.Take ?? maxLimit;
            if (take > maxLimit)
                take = maxLimit;
            q.Limit(dto.Skip, take);

            if (dto.OrderBy != null)
            {
                var fieldNames = dto.OrderBy.Split(FieldSeperators, StringSplitOptions.RemoveEmptyEntries);
                q.OrderByFields(fieldNames);
            }
            else if (dto.OrderByDesc != null)
            {
                var fieldNames = dto.OrderByDesc.Split(FieldSeperators, StringSplitOptions.RemoveEmptyEntries);
                q.OrderByFieldsDescending(fieldNames);
            }
            else if ((dto.Skip != null || dto.Take != null)
                && (options != null && options.OrderByPrimaryKeyOnLimitQuery))
            {
                q.OrderByFields(typeof(From).GetModelMetadata().PrimaryKey);
            }
        }

        private static void AppendJoins(SqlExpression<From> q, IQueryDb dto)
        {
            if (dto is IJoin)
            {
                var dtoInterfaces = dto.GetType().GetInterfaces();
                foreach(var innerJoin in dtoInterfaces.Where(x => x.Name.StartsWith("IJoin`")))
                {
                    var joinTypes = innerJoin.GetGenericArguments();
                    for (var i = 1; i < joinTypes.Length; i++)
                    {
                        q.Join(joinTypes[i - 1], joinTypes[i]);
                    }
                }

                foreach(var leftJoin in dtoInterfaces.Where(x => x.Name.StartsWith("ILeftJoin`")))
                {
                    var joinTypes = leftJoin.GetGenericArguments();
                    for (var i = 1; i < joinTypes.Length; i++)
                    {
                        q.LeftJoin(joinTypes[i - 1], joinTypes[i]);
                    } 
                }
            }
        }

        private static void AppendAutoFilters(SqlExpression<From> q, IQueryDb dto, IAutoQueryOptions options, IRequest req)
        {
            if (AutoFilters.Length == 0)
                return;
            
            var appHost = HostContext.AppHost;
            for (var i = 0; i < AutoFilters.Length; i++)
            {
                var filter = AutoFilters[i];
                var fieldDef = q.ModelDef.GetFieldDefinition(filter.Field);
                if (fieldDef == null)
                    throw new NotSupportedException($"{dto.GetType().Name} '{filter.Field}' AutoFilter was not found on '{typeof(From).Name}'");

                var quotedColumn = q.DialectProvider.GetQuotedColumnName(q.ModelDef, fieldDef);

                var value = appHost.EvalScriptValue(filter, req);

                var dbField = AutoFiltersDbFields[i];
                AddCondition(q, "AND", quotedColumn, value, dbField);
            }
        }

        private static void AppendTypedQueries(SqlExpression<From> q, IQueryDb dto, Dictionary<string, string> dynamicParams, string defaultTerm, IAutoQueryOptions options, Dictionary<string, string> aliases)
        {
            foreach (var entry in PropertyGetters)
            {
                var name = entry.Key.LeftPart('#');

                dynamicParams.Remove(name);

                QueryFieldMap.TryGetValue(name, out var implicitQuery);

                if (implicitQuery?.Field != null)
                    name = implicitQuery.Field;

                var match = GetQueryMatch(q, name, options, aliases);
                if (match == null)
                    continue;

                implicitQuery = implicitQuery == null 
                    ? match.ImplicitQuery 
                    : implicitQuery.Combine(match.ImplicitQuery);

                var quotedColumn = q.DialectProvider.GetQuotedColumnName(match.ModelDef, match.FieldDef);

                var value = entry.Value(dto);
                if (value == null)
                    continue;

                AddCondition(q, defaultTerm, quotedColumn, value, implicitQuery);
            }
        }

        private static void AppendUntypedQueries(SqlExpression<From> q, Dictionary<string, string> dynamicParams, string defaultTerm, IAutoQueryOptions options, Dictionary<string, string> aliases)
        {
            foreach (var entry in dynamicParams)
            {
                var name = entry.Key.LeftPart('#');

                var match = GetQueryMatch(q, name, options, aliases);
                if (match == null)
                    continue;

                var implicitQuery = match.ImplicitQuery;
                var quotedColumn = q.DialectProvider.GetQuotedColumnName(match.ModelDef, match.FieldDef);

                var strValue = !string.IsNullOrEmpty(entry.Value)
                    ? entry.Value
                    : null;
                var fieldType = match.FieldDef.FieldType;
                var isMultiple = (implicitQuery != null && (implicitQuery.ValueStyle > ValueStyle.Single))
                    || string.Compare(name, match.FieldDef.Name + Pluralized, StringComparison.OrdinalIgnoreCase) == 0;

                var value = strValue == null ?
                      null
                    : isMultiple ?
                      TypeSerializer.DeserializeFromString(strValue, Array.CreateInstance(fieldType, 0).GetType())
                    : fieldType == typeof(string) ?
                      strValue
                    : strValue.ChangeTo(fieldType);

                AddCondition(q, defaultTerm, quotedColumn, value, implicitQuery);
            }
        }

        private static void AddCondition(SqlExpression<From> q, string defaultTerm, string quotedColumn, object value, QueryDbFieldAttribute implicitQuery)
        {
            var ret = ExprResult.CreateExpression(defaultTerm, quotedColumn, value, implicitQuery);
            if (ret == null) 
                return;
            
            var result = ret.Value;
            if (implicitQuery?.Term == QueryTerm.Ensure)
            {
                q.Ensure(result.Format, result.Values);
            }
            else
            {
                q.AddCondition(result.DefaultTerm, result.Format, result.Values);
            }
        }

        class MatchQuery
        {
            public MatchQuery(Tuple<ModelDefinition,FieldDefinition> match, QueryDbFieldAttribute implicitQuery)
            {
                ModelDef = match.Item1;
                FieldDef = match.Item2;
                ImplicitQuery = implicitQuery;
            }
            public readonly ModelDefinition ModelDef;
            public readonly FieldDefinition FieldDef;
            public readonly QueryDbFieldAttribute ImplicitQuery;
        }

        private const string Pluralized = "s";

        private static MatchQuery GetQueryMatch(SqlExpression<From> q, string name, IAutoQueryOptions options, Dictionary<string,string> aliases)
        {
            var match = GetQueryMatch(q, name, options);

            if (match == null)
            {
                if (aliases.TryGetValue(name, out var alias))
                    match = GetQueryMatch(q, alias, options);

                if (match == null && JsConfig.TextCase == TextCase.SnakeCase && name.Contains("_"))
                    match = GetQueryMatch(q, name.Replace("_", ""), options);
            }

            return match;
        }

        private static MatchQuery GetQueryMatch(SqlExpression<From> q, string name, IAutoQueryOptions options)
        {
            if (options == null) return null;

            var match = options.IgnoreProperties == null || !options.IgnoreProperties.Contains(name)
                ? q.FirstMatchingField(name) ?? (name.EndsWith(Pluralized) ? q.FirstMatchingField(name.Substring(0, name.Length - 1)) : null)
                : null;

            if (match == null)
            {
                foreach (var startsWith in options.StartsWithConventions)
                {
                    if (name.Length <= startsWith.Key.Length || !name.StartsWith(startsWith.Key)) continue;

                    var field = name.Substring(startsWith.Key.Length);
                    match = q.FirstMatchingField(field) ?? (field.EndsWith(Pluralized) ? q.FirstMatchingField(field.Substring(0, field.Length - 1)) : null);
                    if (match != null)
                        return new MatchQuery(match, startsWith.Value);
                }
            }
            if (match == null)
            {
                foreach (var endsWith in options.EndsWithConventions)
                {
                    if (name.Length <= endsWith.Key.Length || !name.EndsWith(endsWith.Key)) continue;

                    var field = name.Substring(0, name.Length - endsWith.Key.Length);
                    match = q.FirstMatchingField(field) ?? (field.EndsWith(Pluralized) ? q.FirstMatchingField(field.Substring(0, field.Length - 1)) : null);
                    if (match != null)
                        return new MatchQuery(match, endsWith.Value);
                }
            }

            return match != null 
                ? new MatchQuery(match, null) 
                : null;
        }

        public QueryResponse<Into> Execute<Into>(IDbConnection db, ISqlExpression query)
        {
            try
            {
                var q = (SqlExpression<From>)query;

                var include = q.OnlyFields;
                var response = new QueryResponse<Into>
                {
                    Offset = q.Offset.GetValueOrDefault(0),
                    Results = db.LoadSelect<Into, From>(q, include:include),
                };

                return response;
            }
            catch (Exception ex)
            {
                throw new ArgumentException(ex.Message, ex);
            }
        }

        public async Task<QueryResponse<Into>> ExecuteAsync<Into>(IDbConnection db, ISqlExpression query)
        {
            try
            {
                var q = (SqlExpression<From>)query;

                var include = q.OnlyFields;
                var response = new QueryResponse<Into>
                {
                    Offset = q.Offset.GetValueOrDefault(0),
                    Results = await db.LoadSelectAsync<Into, From>(q, include:include),
                };

                return response;
            }
            catch (Exception ex)
            {
                throw new ArgumentException(ex.Message, ex);
            }
        }
    }

    public static class AutoQueryExtensions
    {
        public static QueryDbFieldAttribute Init(this QueryDbFieldAttribute query)
        {
            query.ValueStyle = ValueStyle.Single;
            if (query.Template == null)
                return query;
            if (query.ValueFormat != null 
                && !(query.Template.Contains("{Value1}") || query.Template.Contains("{Values}")))
                return query;

            var i = 0;
            while (query.Template.Contains("{Value" + (i + 1) + "}")) i++;
            if (i > 0)
            {
                query.ValueStyle = ValueStyle.Multiple;
                query.ValueArity = i;
            }
            else
            {
                query.ValueStyle = !query.Template.Contains("{Values}")
                    ? ValueStyle.Single
                    : ValueStyle.List;
            }
            return query;
        }

        public static QueryDbFieldAttribute Combine(this QueryDbFieldAttribute field, QueryDbFieldAttribute convention)
        {
            if (convention == null)
                return field;

            return new QueryDbFieldAttribute
            {
                Term = field.Term,
                Operand = field.Operand ?? convention.Operand,
                Template = field.Template ?? convention.Template,
                Field = field.Field ?? convention.Field,
                ValueFormat = field.ValueFormat ?? convention.ValueFormat,
                ValueStyle = field.ValueStyle,
                ValueArity = field.ValueArity != 0 ? field.ValueArity : convention.ValueArity,
            };
        }

        public static SqlExpression<From> CreateQuery<From>(this IAutoQueryDb autoQuery, IQueryDb<From> model, IRequest request)
        {
            return autoQuery.CreateQuery(model, request.GetRequestParams(), request);
        }

        public static SqlExpression<From> CreateQuery<From>(this IAutoQueryDb autoQuery, IQueryDb<From> model, IRequest request, IDbConnection db)
        {
            return autoQuery.CreateQuery(model, request.GetRequestParams(), request, db);
        }

        public static SqlExpression<From> CreateQuery<From, Into>(this IAutoQueryDb autoQuery, IQueryDb<From, Into> model, IRequest request)
        {
            return autoQuery.CreateQuery(model, request.GetRequestParams(), request);
        }

        public static SqlExpression<From> CreateQuery<From, Into>(this IAutoQueryDb autoQuery, IQueryDb<From, Into> model, IRequest request, IDbConnection db)
        {
            return autoQuery.CreateQuery(model, request.GetRequestParams(), request, db);
        }
    }
}