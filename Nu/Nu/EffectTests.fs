﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2018.

namespace Nu.Tests
open System
open Xunit
open Prime
open Nu
module EffectSystemTests =

    let [<Fact>] readEffectWorks () =
        Math.init ()
        let effectStr =
            "[TestEffect None []
              [Emit
               [Shift 0.1]
               [Rate 1]
               [[Rotation Sum Ease Bounce [[-1 30] [1 0]]]]
               [[Translation Sum EaseOut Once [[[0 0] 180] [[80 500] 0]]]
                [Size Scale Linear Once [[[0.1 0.1] 180] [[5 3] 0]]]
                [Color Set Linear Once [[[1 0 1 1] 180] [[1 1 0 0] 0]]]]
               [StaticSprite
                [Resource Default Image] [] Nil]]]"
        let effect = scvalue<Effect> effectStr
        Assert.Equal<string> ("TestEffect", effect.EffectName) // TODO: more assertions