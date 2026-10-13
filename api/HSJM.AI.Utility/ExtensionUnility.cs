using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace HSJM.AI.Utility
{
    /// <summary>
    /// 拓展工具类
    /// </summary>
    public static class ExtensionUnility
    {
        public static Expression<Func<T, object>> CreateOrderByExpression<T>(this string propertyName)
        {
            // 检查属性是否存在
            var property = typeof(T).GetProperties().FirstOrDefault(it => it.Name.ToLower() == propertyName.ToLower());
            if (property == null)
                throw new ArgumentException($"Type '{typeof(T).Name}' does not contain a property named '{propertyName}'.");

            // 创建 lambda 表达式
            ParameterExpression parameter = Expression.Parameter(typeof(T), "x");
            MemberExpression propertyAccess = Expression.Property(parameter, property);
            UnaryExpression castToObject = Expression.Convert(propertyAccess, typeof(object));
            return Expression.Lambda<Func<T, object>>(castToObject, parameter);
        }
        public static Expression<Func<T, bool>> GetTrue<T>() { return f => true; }
        public static Expression<Func<T, bool>> GetFalse<T>() { return f => false; }

        public static Expression<Func<T, bool>> And<T>(this Expression<Func<T, bool>> first, Expression<Func<T, bool>> second)
        {
            return first.AndAlso<T>(second, Expression.AndAlso);
        }

        public static Expression<Func<T, bool>> Or<T>(this Expression<Func<T, bool>> first, Expression<Func<T, bool>> second)
        {
            return first.AndAlso<T>(second, Expression.OrElse);
        }

        private static Expression<Func<T, bool>> AndAlso<T>(this Expression<Func<T, bool>> expr1, Expression<Func<T, bool>> expr2, Func<Expression, Expression, BinaryExpression> func)
        {
            var parameter = Expression.Parameter(typeof(T));

            var leftVisitor = new ReplaceExpressionVisitor(expr1.Parameters[0], parameter);
            var left = leftVisitor.Visit(expr1.Body);

            var rightVisitor = new ReplaceExpressionVisitor(expr2.Parameters[0], parameter);
            var right = rightVisitor.Visit(expr2.Body);

            return Expression.Lambda<Func<T, bool>>(
                func(left, right), parameter);
        }

        private class ReplaceExpressionVisitor : ExpressionVisitor
        {
            private readonly Expression oldValue;
            private readonly Expression newValue;

            public ReplaceExpressionVisitor(Expression oldValue, Expression newValue)
            {
                this.oldValue = oldValue;
                this.newValue = newValue;
            }

            public override Expression Visit(Expression node)
            {
                if (node == this.oldValue)
                    return this.newValue;
                return base.Visit(node);
            }
        }
        /// <summary>
        /// 将对象转成int类型
        /// </summary>
        /// <param name="val"></param>
        /// <returns></returns>
        public static int ToInt(this object val)
        {
            return Convert.ToInt32(val);
        }

        /// <summary>
        /// 将对象转成long类型
        /// </summary>
        /// <param name="val"></param>
        /// <returns></returns>
        public static long ToLong(this object val)
        {
            return Convert.ToInt64(val);

        }

        /// <summary>
        /// IEnumerable 类型是否为null或空
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <returns></returns>
        public static bool IsListNullOrEmpty<T>(this IEnumerable<T> value)
        {
            if (value != null && value.Any())
            {
                return false;
            }
            return true;
        }
    }
}
