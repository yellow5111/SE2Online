using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Keen.VRage.Animation.Client.GameObjects;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// A character the client builds from the host's world bundle can be registered with its animator after this tick's
/// animation controller job already ran. AnimatorPoseUpdate then finds no AnimatorRequest for it, asserts and throws a
/// NullReferenceException in FootInFrontClipSwapperComponent, which crashes the join. Skipping that animator's pose for
/// the one frame lets the controller fill the request on the next tick.
/// </summary>
internal static class AnimatorGuard
{
	private static Func<MultiStrategyAnimatorComponent, bool>? _missingRequest;
	private static bool _logged;

	internal static void Install(Harmony harmony)
	{
		_missingRequest = BuildCheck();
		harmony.Patch(AccessTools.Method(typeof(MultiStrategyAnimatorComponent), "AnimatorPoseUpdate"), prefix: new HarmonyMethod(typeof(AnimatorGuard), nameof(Prefix)));
	}

	// comp => any registered strategy has a null AnimatorRequest, compiled once (the strategy struct is private).
	private static Func<MultiStrategyAnimatorComponent, bool> BuildCheck()
	{
		FieldInfo strategies = AccessTools.Field(typeof(MultiStrategyAnimatorComponent), "_registeredStrategies");
		Type entryType = strategies.FieldType.GetElementType()!;
		FieldInfo strategy = AccessTools.Field(entryType, "Strategy");
		PropertyInfo request = AccessTools.Property(strategy.FieldType, "AnimatorRequest");

		var comp = Expression.Parameter(typeof(MultiStrategyAnimatorComponent), "comp");
		var array = Expression.Variable(strategies.FieldType, "array");
		var i = Expression.Variable(typeof(int), "i");
		var result = Expression.Variable(typeof(bool), "result");
		var end = Expression.Label("end");
		var body = Expression.Block(new[] { array, i, result },
			Expression.Assign(array, Expression.Field(comp, strategies)),
			Expression.IfThen(Expression.Equal(array, Expression.Constant(null, strategies.FieldType)), Expression.Return(end)),
			Expression.Assign(i, Expression.Constant(0)),
			Expression.Loop(
				Expression.IfThenElse(Expression.LessThan(i, Expression.ArrayLength(array)),
					Expression.Block(
						Expression.IfThen(
							Expression.OrElse(
								Expression.Equal(Expression.Field(Expression.ArrayAccess(array, i), strategy), Expression.Constant(null, strategy.FieldType)),
								Expression.Equal(Expression.Property(Expression.Field(Expression.ArrayAccess(array, i), strategy), request), Expression.Constant(null, request.PropertyType))),
							Expression.Block(Expression.Assign(result, Expression.Constant(true)), Expression.Return(end))),
						Expression.PostIncrementAssign(i)),
					Expression.Return(end))),
			Expression.Label(end),
			result);
		return Expression.Lambda<Func<MultiStrategyAnimatorComponent, bool>>(body, comp).Compile();
	}

	private static bool Prefix(MultiStrategyAnimatorComponent __instance)
	{
		if (_missingRequest == null || !_missingRequest(__instance)) return true;
		if (!_logged)
		{
			_logged = true;
			GameLog.Instance.Warn("animator guard: a character's animation request was not ready this frame; its pose waits a frame instead of crashing");
		}
		return false;
	}
}
