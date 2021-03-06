﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Pathoschild.WebApi.NhibernateOdata.Internal
{
	/// <summary>Intercepts queries before they're parsed by NHibernate to rewrite unsupported nullable-boolean conditions into supported boolean conditions.</summary>
	public class FixNullableBooleanVisitor : ExpressionVisitor
	{
		/*********
		** Properties
		*********/
		/// <summary>Whether the visitor is visiting a nested node.</summary>
		/// <remarks>This is used to recognize the top-level node for logging.</remarks>
		protected bool IsRecursing = false;

		/// <summary>The nodes to rewrite.</summary>
		protected readonly HashSet<Expression> NodeRewriteList = new HashSet<Expression>();


		/*********
		** Public methods
		*********/
		/// <summary>Dispatches the expression to one of the more specialized visit methods in this class.</summary>
		/// <param name="node">The expression to visit.</param>
		/// <returns>The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.</returns>
		public override Expression Visit(Expression node)
		{
			// top node
			if (!this.IsRecursing)
			{
				this.IsRecursing = true;
				return base.Visit(node);
			}

			// nested node
			if (node is ConstantExpression)
				return this.VisitConstant(node as ConstantExpression);
			if (node is ConditionalExpression)
				return this.VisitConditional(node as ConditionalExpression);
			if (node is BinaryExpression)
				return this.VisitBinary(node as BinaryExpression);
			return base.Visit(node);
		}

		/***
		** ExpressionVisitor
		***/
		/// <summary>Visits the <see cref="T:System.Linq.Expressions.ConstantExpression"/>.</summary>
		/// <param name="node">The expression to visit.</param>
		/// <returns>The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.</returns>
		protected override Expression VisitConstant(ConstantExpression node)
		{
			return this.SwitchVisit(
				node,
				type => Expression.Constant(node.Value ?? Activator.CreateInstance(type), type), // rewrite Nullable<T> with T ?? default(T)
				() => node
			);
		}

		/// <summary>Visits the children of the <see cref="T:System.Linq.Expressions.BinaryExpression"/>.</summary>
		/// <param name="node">The expression to visit.</param>
		/// <returns>The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.</returns>
		protected override Expression VisitBinary(BinaryExpression node)
		{
			switch (node.NodeType)
			{
				case ExpressionType.AndAlso:
					{
						// ensure operands are not nullable
						this.MarkForRewrite(node.Left, node.Right);
						Expression left = this.Visit(node.Left);
						Expression right = this.Visit(node.Right);

						// we can't have one nullable and one non-nullable. We can add a cast to nullable, NHibernate seems to handle this fine.
						if (left.Type != right.Type)
						{
							if (left.Type == typeof(bool))
								left = Expression.Convert(left, typeof(bool?));

							if (right.Type == typeof(bool))
								right = Expression.Convert(right, typeof(bool?));
						}

						node = Expression.AndAlso(left, right, node.Method);
					}
					break;

				case ExpressionType.OrElse:
					{
						// ensure operands are not nullable
						this.MarkForRewrite(node.Left, node.Right);
						Expression left = this.Visit(node.Left);
						Expression right = this.Visit(node.Right);

						// we can't have one nullable and one non-nullable. We can add a cast to nullable, NHibernate seems to handle this fine.
						if (left.Type != right.Type)
						{
							if (left.Type == typeof(bool))
								left = Expression.Convert(left, typeof(bool?));

							if (right.Type == typeof(bool))
								right = Expression.Convert(right, typeof(bool?));
						}

						node = Expression.OrElse(left, right, node.Method);
					}
					break;

				case ExpressionType.Equal:
					{
						Expression left = this.Visit(node.Left);
						Expression right = this.Visit(node.Right);

						// ensure operand types match (they can fall out of sync if one of them was rewritten)
						if (left.Type != right.Type)
						{
							if (new[] { left, right }.All(p => p.Type != typeof(object) && !this.ShouldRewrite(p)))
							{
								this.MarkForRewrite(node.Left, node.Right);
								left = this.Visit(left);
								right = this.Visit(right);
							}
						}

						// recreate node with IsLiftedToNull=false to prevent a Nullable<bool> operator value
						node = Expression.Equal(left, right, false/*liftToNull*/, node.Method);
						break;
					}

				default:
					node = node.Update(this.Visit(node.Left), this.VisitAndConvert(node.Conversion, "VisitBinary"), this.Visit(node.Right));
					break;
			}

			return node;
		}

		/// <summary>Visits the children of the <see cref="T:System.Linq.Expressions.UnaryExpression"/>.</summary>
		/// <param name="node">The expression to visit.</param>
		/// <returns>The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.</returns>
		protected override Expression VisitUnary(UnaryExpression node)
		{
			Expression operand = this.Visit(node.Operand);
			return this.SwitchVisit(
				node,
				type =>
				{
					if (node.NodeType == ExpressionType.Convert && operand.Type == type)
						return operand;
					else
						return node.Update(operand);
				},
				() => node.Update(operand)
			);
		}

		/***
		** Protected methods
		***/
		/// <summary>Defines the behaviour for visiting a node depending on whether it should be rewritten.</summary>
		/// <typeparam name="TExpression">The expression type.</typeparam>
		/// <param name="node">The node to visit.</param>
		/// <param name="rewrite">Get the expression when it should be rewritten.</param>
		/// <param name="fallback">Get the expression when it should be visited without rewriting.</param>
		/// <param name="forceRewrite">Always rewrite the node if the type is <see cref="Nullable{T}"/>, even if the node is not in the <see cref="NodeRewriteList"/>.</param>
		protected TExpression SwitchVisit<TExpression>(TExpression node, Func<Type, TExpression> rewrite, Func<TExpression> fallback, bool forceRewrite = false)
			where TExpression : Expression
		{
			if (this.ShouldRewrite(node, forceRewrite))
			{
				Type type = Nullable.GetUnderlyingType(node.Type);
				TExpression result = rewrite(type);
				return result;
			}
			return fallback();
		}

		/// <summary>Get whether the node should be rewritten.</summary>
		/// <param name="node">The node to rewrite.</param>
		/// <param name="forceRewrite">Always rewrite the node if the type is <see cref="Nullable{T}"/>, even if the node is not in the <see cref="NodeRewriteList"/>.</param>
		protected bool ShouldRewrite(Expression node, bool forceRewrite = false)
		{
			return (forceRewrite || this.NodeRewriteList.Contains(node)) && Nullable.GetUnderlyingType(node.Type) != null;
		}

		/// <summary>Add nodes to the <see cref="NodeRewriteList"/> so they'll be rewritten when visited.</summary>
		/// <param name="nodes">The nodes to mark.</param>
		protected void MarkForRewrite(params Expression[] nodes)
		{
			foreach (Expression node in nodes)
				this.NodeRewriteList.Add(node);
		}
	}
}
