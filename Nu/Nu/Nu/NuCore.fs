﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2014.

namespace Nu
open System
open System.Configuration
open System.Text
open Prime

[<AutoOpen>]
module NuCoreModule =

    /// Specifies the screen-clearing routine.
    type ScreenClear =
        | NoClear
        | ColorClear of byte * byte * byte

    /// Specifies whether the engine is running or exiting.
    type Liveness =
        | Running
        | Exiting

[<RequireQualifiedAccess>]
module NuCore =

    /// The invalid Id.
    let InvalidId = Guid.Empty

    /// Make a Nu Id.
    let makeId = Guid.NewGuid

    /// Get a resolution along either an X or Y dimension.
    let getResolutionOrDefault isX defaultResolution =
        let resolution = ref 0
        let appSetting = ConfigurationManager.AppSettings.["Resolution" + if isX then "X" else "Y"]
        if not <| Int32.TryParse (appSetting, resolution) then resolution := defaultResolution
        !resolution