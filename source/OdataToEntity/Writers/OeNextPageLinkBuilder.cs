﻿using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using OdataToEntity.Parsers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OdataToEntity.Writers
{
    public readonly struct OeNextPageLinkBuilder
    {
        private readonly OeQueryContext _queryContext;

        public OeNextPageLinkBuilder(OeQueryContext queryContext)
        {
            _queryContext = queryContext;
        }

        public static int GetCount(IEdmModel edmModel, OeEntryFactory entryFactory, Object value)
        {
            ODataUri odataUri = OeNextPageLinkBuilder.GetCountODataUri(edmModel, entryFactory, entryFactory.NavigationSelectItem, value);
            var queryContext = new OeQueryContext(edmModel, odataUri, 0, false, OeMetadataLevel.Minimal);

            IEdmEntitySet entitySet = (odataUri.Path.FirstSegment as EntitySetSegment).EntitySet;
            Db.OeDataAdapter dataAdapter = edmModel.GetDataAdapter(entitySet.Container);
            Object dataContext = null;
            try
            {
                dataContext = dataAdapter.CreateDataContext();
                return dataAdapter.ExecuteScalar<int>(dataContext, queryContext);
            }
            finally
            {
                if (dataContext != null)
                    dataAdapter.CloseDataContext(dataContext);
            }
        }
        private static ODataUri GetCountODataUri(IEdmModel edmModel, OeEntryFactory entryFactory, ExpandedNavigationSelectItem item, Object value)
        {
            FilterClause filterClause = GetFilter(edmModel, entryFactory, item, value);
            var entitytSet = (IEdmEntitySet)(filterClause.RangeVariable as ResourceRangeVariable).NavigationSource;
            var pathSegments = new ODataPathSegment[] { new EntitySetSegment(entitytSet) { Identifier = entitytSet.Name }, CountSegment.Instance };

            return new ODataUri()
            {
                Filter = filterClause,
                Path = new ODataPath(pathSegments),
            };
        }
        private static FilterClause GetFilter(IEdmModel edmModel, OeEntryFactory entryFactory, ExpandedNavigationSelectItem item, Object value)
        {
            SingleValueNode filterExpression;
            ResourceRangeVariableReferenceNode refNode;

            var segment = (NavigationPropertySegment)item.PathToNavigationProperty.LastSegment;
            IEdmNavigationProperty navigationProperty = segment.NavigationProperty;
            if (navigationProperty.ContainsTarget)
            {
                ModelBuilder.ManyToManyJoinDescription joinDescription = edmModel.GetManyToManyJoinDescription(navigationProperty);
                navigationProperty = joinDescription.JoinNavigationProperty.Partner;

                IEdmEntitySet joinNavigationSource = OeEdmClrHelper.GetEntitySet(edmModel, joinDescription.JoinNavigationProperty);
                ResourceRangeVariableReferenceNode joinRefNode = OeEdmClrHelper.CreateRangeVariableReferenceNode(joinNavigationSource, "d");

                IEdmEntitySet targetNavigationSource = OeEdmClrHelper.GetEntitySet(edmModel, joinDescription.TargetNavigationProperty);
                ResourceRangeVariableReferenceNode targetRefNode = OeEdmClrHelper.CreateRangeVariableReferenceNode(targetNavigationSource);

                var anyNode = new AnyNode(new Collection<RangeVariable>() { joinRefNode.RangeVariable, targetRefNode.RangeVariable }, joinRefNode.RangeVariable)
                {
                    Source = new CollectionNavigationNode(targetRefNode, joinDescription.TargetNavigationProperty.Partner, null),
                    Body = OeGetParser.CreateFilterExpression(joinRefNode, GetKeysFromParentValue(navigationProperty))
                };

                refNode = targetRefNode;
                filterExpression = anyNode;
            }
            else
            {
                IEdmNavigationProperty dependentNavigationProperty = navigationProperty.IsPrincipal() ? navigationProperty.Partner : navigationProperty;

                refNode = OeEdmClrHelper.CreateRangeVariableReferenceNode((IEdmEntitySetBase)segment.NavigationSource);
                List<KeyValuePair<IEdmStructuralProperty, Object>> keys;
                if (entryFactory.EdmNavigationProperty == navigationProperty)
                    keys = GetKeysSelfValue(dependentNavigationProperty);
                else
                    keys = GetKeysFromParentValue(dependentNavigationProperty);
                filterExpression = OeGetParser.CreateFilterExpression(refNode, keys);
            }

            if (item.FilterOption != null)
                filterExpression = new BinaryOperatorNode(BinaryOperatorKind.And, filterExpression, item.FilterOption.Expression);

            return new FilterClause(filterExpression, refNode.RangeVariable);

            List<KeyValuePair<IEdmStructuralProperty, Object>> GetKeysFromParentValue(IEdmNavigationProperty edmNavigationProperty)
            {
                var keys = new List<KeyValuePair<IEdmStructuralProperty, Object>>();
                IEnumerator<IEdmStructuralProperty> dependentProperties = edmNavigationProperty.DependentProperties().GetEnumerator();
                foreach (IEdmStructuralProperty key in edmNavigationProperty.PrincipalProperties())
                {
                    dependentProperties.MoveNext();
                    Object keyValue = entryFactory.GetAccessorByName(key.Name).GetValue(value);
                    keys.Add(new KeyValuePair<IEdmStructuralProperty, Object>(dependentProperties.Current, keyValue));
                }
                return keys;
            }
            List<KeyValuePair<IEdmStructuralProperty, Object>> GetKeysSelfValue(IEdmNavigationProperty edmNavigationProperty)
            {
                var keys = new List<KeyValuePair<IEdmStructuralProperty, Object>>();
                foreach (IEdmStructuralProperty key in edmNavigationProperty.DependentProperties())
                {
                    Object keyValue = entryFactory.GetAccessorByName(key.Name).GetValue(value);
                    keys.Add(new KeyValuePair<IEdmStructuralProperty, Object>(key, keyValue));
                }
                return keys;
            }
        }
        private static KeyValuePair<String, Object>[] GetNavigationSkipTokenKeys(OeEntryFactory entryFactory, OrderByClause orderByClause, Object value)
        {
            IEdmStructuralProperty[] keyProperties = OeSkipTokenParser.GetEdmProperies(orderByClause);
            var keys = new KeyValuePair<String, Object>[keyProperties.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                String propertyName = OeSkipTokenParser.GetPropertyName(keyProperties[i]);
                OePropertyAccessor accessor = entryFactory.GetAccessorByName(keyProperties[i].Name);
                keys[i] = new KeyValuePair<String, Object>(propertyName, accessor.GetValue(value));
            }
            return keys;
        }
        public static Uri GetNavigationUri(IEdmModel edmModel, OeEntryFactory entryFactory, ExpandedNavigationSelectItem item, Object value)
        {
            return GetNavigationUri(edmModel, entryFactory, item, item.OrderByOption, value, null);
        }
        private static Uri GetNavigationUri(IEdmModel edmModel, OeEntryFactory entryFactory,
            ExpandedNavigationSelectItem item, OrderByClause orderByClause, Object value, String skipToken)
        {
            bool? queryCount = item.CountOption;
            long? top = item.TopOption;
            if (skipToken != null)
            {
                queryCount = null;
                if (entryFactory.PageSize > 0 && (top == null || entryFactory.PageSize < top.GetValueOrDefault()))
                    top = entryFactory.PageSize;
            }

            FilterClause filterClause = GetFilter(edmModel, entryFactory, item, value);
            var entitytSet = (IEdmEntitySet)(filterClause.RangeVariable as ResourceRangeVariable).NavigationSource;
            var pathSegments = new ODataPathSegment[] { new EntitySetSegment(entitytSet) };

            var odataUri = new ODataUri()
            {
                Filter = filterClause,
                OrderBy = orderByClause,
                Path = new ODataPath(pathSegments),
                QueryCount = queryCount,
                SelectAndExpand = item.SelectAndExpand,
                Skip = item.SkipOption,
                SkipToken = skipToken,
                Top = top
            };
            return odataUri.BuildUri(ODataUrlKeyDelimiter.Parentheses);
        }
        public Uri GetNextPageLinkNavigation(OeEntryFactory entryFactory, int readCount, long? totalCount, Object value)
        {
            if (entryFactory.PageSize == 0 || readCount == 0 || (totalCount != null && readCount >= totalCount))
                return null;

            ExpandedNavigationSelectItem item = entryFactory.NavigationSelectItem;
            OrderByClause orderByClause = OeSkipTokenParser.GetUniqueOrderBy(entryFactory.EntitySet, item.OrderByOption, null);
            KeyValuePair<String, Object>[] keys = GetNavigationSkipTokenKeys(entryFactory, orderByClause, value);

            int restCount = GetRestCountNavigation((int?)item.TopOption, readCount, totalCount);
            String skipToken = OeSkipTokenParser.GetSkipToken(_queryContext.EdmModel, keys, restCount);
            return GetNavigationUri(_queryContext.EdmModel, entryFactory, item, orderByClause, value, skipToken);
        }
        public Uri GetNextPageLinkRoot(OeEntryFactory entryFactory, int readCount, int? totalCount, Object value)
        {
            if (readCount == 0)
                return null;

            int pageSize = GetPageSizeRoot(entryFactory);
            if (pageSize == 0)
                return null;

            int restCount = GetRestCountRoot(pageSize, readCount, totalCount);
            if (restCount == 0)
                return null;

            ODataUri nextOdataUri = _queryContext.ODataUri.Clone();
            nextOdataUri.ServiceRoot = null;
            nextOdataUri.QueryCount = null;
            nextOdataUri.Top = pageSize;
            nextOdataUri.Skip = null;
            nextOdataUri.SkipToken = OeSkipTokenParser.GetSkipToken(_queryContext.EdmModel, entryFactory.SkipTokenAccessors, value, restCount);

            return nextOdataUri.BuildUri(ODataUrlKeyDelimiter.Parentheses);
        }
        private int GetPageSizeRoot(OeEntryFactory entryFactory)
        {
            int pageSize = entryFactory.PageSize;
            if (pageSize == 0)
            {
                pageSize = _queryContext.MaxPageSize;
                if (pageSize == 0 && _queryContext.ODataUri.SkipToken != null && _queryContext.ODataUri.Top.GetValueOrDefault() > 0)
                    pageSize = (int)_queryContext.ODataUri.Top.GetValueOrDefault();
            }
            else
            {
                if (_queryContext.MaxPageSize > 0 && _queryContext.MaxPageSize < pageSize)
                    pageSize = _queryContext.MaxPageSize;
            }

            return pageSize;
        }
        private static int GetRestCountNavigation(int? top, int readCount, long? totalCount)
        {
            if (totalCount != null && totalCount < top.GetValueOrDefault())
                top = (int)totalCount.GetValueOrDefault();

            return (top ?? Int32.MaxValue) - readCount;
        }
        private int GetRestCountRoot(int pageSize, int readCount, int? totalCount)
        {
            int? restCount;
            if (totalCount == null)
                restCount = _queryContext.RestCount;
            else
                restCount = totalCount;

            if (readCount > 0 && (restCount == null || restCount.GetValueOrDefault() > 0))
            {
                if (restCount == null)
                {
                    if (_queryContext.ODataUri.Top.GetValueOrDefault() > 0)
                        restCount = (int)_queryContext.ODataUri.Top.GetValueOrDefault() - readCount;
                    else
                        restCount = Int32.MaxValue;
                }
                else
                    restCount = restCount.GetValueOrDefault() - readCount;
            }

            return restCount.GetValueOrDefault();
        }
    }
}