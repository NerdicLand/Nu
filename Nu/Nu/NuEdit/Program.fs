﻿namespace NuEdit
open NuEditDesign
open SDL2
open OpenTK
open TiledSharp
open Miscellanea
open System
open System.IO
open System.Collections.Generic
open System.Reflection
open System.Windows.Forms
open System.ComponentModel
open System.Xml
open System.Xml.Serialization
open Microsoft.FSharp.Reflection
open Nu
open Nu.Core
open Nu.Voords
open Nu.Constants
open Nu.Math
open Nu.Metadata
open Nu.Physics
open Nu.Sdl
open Nu.Entities
open Nu.GroupModule
open Nu.ScreenModule
open Nu.GameModule
open Nu.WorldModule
open NuEdit.Constants
open NuEdit.Reflection

type WorldChanger = World -> World
type WorldChangers = WorldChanger List

type DragEntityState =
    | DragEntityNone
    | DragEntityPosition of Vector2 * Vector2 * Address
    | DragEntityRotation of Vector2 * Vector2 * Address

type DragCameraState =
    | DragCameraNone
    | DragCameraPosition of Vector2 * Vector2

type EditorState =
    { DragEntityState : DragEntityState
      DragCameraState : DragCameraState
      PastWorlds : World list
      FutureWorlds : World list
      Clipboard : (EntityModel option) ref }

module Program =

    let DefaultPositionSnap = 8
    let DefaultRotationSnap = 5
    let DefaultCreationDepth = 0.0f
    let CameraSpeed = 4.0f // NOTE: might be nice to be able to configure this just like entity creation depth in the editor

    let pushPastWorld pastWorld world =
        let editorState_ = world.ExtData :?> EditorState
        let editorState_ = { editorState_ with PastWorlds = pastWorld :: editorState_.PastWorlds; FutureWorlds = [] }
        { world with ExtData = editorState_ }

    let clearPastWorlds world =
        let editorState_ = world.ExtData :?> EditorState
        let editorState_ = { editorState_ with PastWorlds = [] }
        { world with ExtData = editorState_ }

    type [<TypeDescriptionProvider (typeof<EntityModelTypeDescriptorProvider>)>] EntityModelTypeDescriptorSource =
        { Address : Address
          Form : NuEditForm
          WorldChangers : WorldChangers
          RefWorld : World ref }

    and EntityModelPropertyDescriptor (property) =
        inherit PropertyDescriptor ((match property with XFieldDescriptor x -> x.FieldName.LunStr | PropertyInfo p -> p.Name), Array.empty)

        let propertyName = match property with XFieldDescriptor x -> x.FieldName.LunStr | PropertyInfo p -> p.Name
        let propertyType = match property with XFieldDescriptor x -> findType x.TypeName.LunStr | PropertyInfo p -> p.PropertyType

        override this.ComponentType with get () = propertyType.DeclaringType
        override this.PropertyType with get () = propertyType
        override this.CanResetValue source = false
        override this.ResetValue source = ()
        override this.ShouldSerializeValue source = true

        override this.IsReadOnly
            // NOTE: we make ids read-only
            with get () =
                // TODO: find an remove duplication of this expression
                propertyName.EndsWith "Id" || propertyName.EndsWith "Ids"

        override this.GetValue optSource =
            match optSource with
            | null -> null
            | source ->
                let entityModelTds = source :?> EntityModelTypeDescriptorSource
                let entityModelLens = worldEntityModelLens entityModelTds.Address
                let entityModel = get !entityModelTds.RefWorld entityModelLens
                getEntityModelPropertyValue property entityModel

        override this.SetValue (source, value) =
            let entityModelTds = source :?> EntityModelTypeDescriptorSource
            let changer = (fun world ->
                let pastWorld = world
                let world_ =
                    // handle special case for an entity's Name field change
                    if propertyName = "Name" then
                        let valueStr = str value
                        if Int64.TryParse (valueStr, ref 0L) then
                            trace <| "Invalid entity model name '" + valueStr + "' (must not be a number)."
                            world
                        else
                            // TODO: factor out a renameEntityModel function
                            let entityModel_ = get world <| worldEntityModelLens entityModelTds.Address
                            let world_ = removeEntityModel entityModelTds.Address world
                            let entity_ = get entityModel_ entityLens
                            let entity_ = { entity_ with Name = valueStr }
                            let entityModel_ = set entity_ entityModel_ entityLens
                            let entityAddress = addrstr EditorGroupAddress valueStr
                            let world_ = addEntityModel entityAddress entityModel_ world_
                            entityModelTds.RefWorld := world_ // must be set for property grid
                            entityModelTds.Form.propertyGrid.SelectedObject <- { entityModelTds with Address = entityAddress }
                            world_
                    else
                        let world_ = setEntityModelPropertyValue entityModelTds.Address property value world
                        let entityModel_ = get world_ <| worldEntityModelLens entityModelTds.Address
                        let entityModel_ =
                            // handle special case for TileMap's TileMapAsset field change
                            if propertyName = "TileMapAsset" then
                                match entityModel_ with
                                | TileMap tileMap_ ->
                                    let tileMapAsset = tileMap_.TileMapAsset
                                    let optTileMapMetadata = tryGetTileMapMetadata tileMapAsset.TileMapAssetName tileMapAsset.PackageName world_.AssetMetadataMap
                                    match optTileMapMetadata with
                                    | None -> entityModel_
                                    | Some (tileMapFileName, tileMapSprites) ->
                                        let tileMap_ = { tileMap_ with TmxMap = TmxMap tileMapFileName }
                                        let tileMap_ = { tileMap_ with TileMapSprites = tileMapSprites }
                                        TileMap tileMap_
                                | _ -> entityModel_
                            else entityModel_
                        let world_ = set entityModel_ world_ <| worldEntityModelLens entityModelTds.Address
                        propagateEntityModelPhysics entityModelTds.Address entityModel_ world_
                pushPastWorld pastWorld world_)
            entityModelTds.RefWorld := changer !entityModelTds.RefWorld
            entityModelTds.WorldChangers.Add changer

        // NOTE: This has to be a static member in order to see the relevant types in the recursive definitions.
        static member GetPropertyDescriptors (aType : Type) optSource =
            let properties = aType.GetProperties (BindingFlags.Instance ||| BindingFlags.Public)
            let propertyDescriptors =
                Seq.map
                    (fun property -> EntityModelPropertyDescriptor (NuEdit.PropertyInfo property) :> PropertyDescriptor)
                    properties
            let optProperty = Array.tryFind (fun (property : PropertyInfo) -> property.PropertyType = typeof<Xtension>) properties
            let propertyDescriptors' =
                match (optProperty, optSource) with
                | (None, _) 
                | (_, None) -> propertyDescriptors
                | (Some property, Some source) ->
                    let entity = get source entityLens
                    let xtension = property.GetValue entity :?> Xtension
                    let xFieldDescriptors =
                        Seq.map
                            (fun (xField : KeyValuePair<Lun, obj>) ->
                                let fieldName = xField.Key
                                let typeName = Lun.make (xField.Value.GetType ()).FullName
                                let xFieldDescriptor = NuEdit.XFieldDescriptor { FieldName = fieldName; TypeName = typeName }
                                EntityModelPropertyDescriptor xFieldDescriptor :> PropertyDescriptor)
                            xtension.XFields
                    Seq.append propertyDescriptors xFieldDescriptors
            List.ofSeq propertyDescriptors'

    and EntityModelTypeDescriptor (optSource : obj) =
        inherit CustomTypeDescriptor ()
        override this.GetProperties _ =
            let propertyDescriptors =
                match optSource with
                | :? EntityModelTypeDescriptorSource as source ->
                    let entityModelLens = worldEntityModelLens source.Address
                    let entityModel = get !source.RefWorld entityModelLens
                    let entityModelTypes = getEntityModelTypes entityModel
                    // NOTE: this line could be simplified by a List.concatBy function.
                    List.fold (fun propertyDescriptors aType -> EntityModelPropertyDescriptor.GetPropertyDescriptors aType (Some entityModel) @ propertyDescriptors) [] entityModelTypes
                | _ -> EntityModelPropertyDescriptor.GetPropertyDescriptors typeof<EntityModel> None
            PropertyDescriptorCollection (Array.ofList propertyDescriptors)

    and EntityModelTypeDescriptorProvider () =
        inherit TypeDescriptionProvider ()
        override this.GetTypeDescriptor (_, optSource) =
            EntityModelTypeDescriptor optSource :> ICustomTypeDescriptor

    let getSnaps (form : NuEditForm) =
        let positionSnap = ref 0
        ignore <| Int32.TryParse (form.positionSnapTextBox.Text, positionSnap)
        let rotationSnap = ref 0
        ignore <| Int32.TryParse (form.rotationSnapTextBox.Text, rotationSnap)
        (!positionSnap, !rotationSnap)
    
    let getCreationDepth (form : NuEditForm) =
        let creationDepth = ref 0.0f
        ignore <| Single.TryParse (form.creationDepthTextBox.Text, creationDepth)
        !creationDepth

    let beginEntityDrag (form : NuEditForm) worldChangers refWorld _ _ message world =
        match message.Data with
        | MouseButtonData (position, _) ->
            if form.interactButton.Checked then (message, true, world)
            else
                let group = get world (worldGroupLens EditorGroupAddress)
                let entityModels = Map.toValueList (get world <| worldEntityModelsLens EditorGroupAddress)
                let optPicked = tryPick position entityModels world
                match optPicked with
                | None -> (handle message, true, world)
                | Some entityModel ->
                    let pastWorld = world
                    let entity = get entityModel entityLens
                    let entityAddress = addrstr EditorGroupAddress entity.Name
                    let entityTransform = getEntityModelTransform (Some world.Camera) (xdc world) entityModel
                    let dragState = DragEntityPosition (entityTransform.Position + world.MouseState.MousePosition, world.MouseState.MousePosition, entityAddress)
                    let editorState_ = world.ExtData :?> EditorState
                    let editorState_ = { editorState_ with DragEntityState = dragState }
                    let world_ = { world with ExtData = editorState_ }
                    let world_ = pushPastWorld pastWorld world_
                    refWorld := world_ // must be set for property grid
                    form.propertyGrid.SelectedObject <- { Address = entityAddress; Form = form; WorldChangers = worldChangers; RefWorld = refWorld }
                    (handle message, true, world_)
        | _ -> failwith <| "Expected MouseButtonData in message '" + str message + "'."

    let endEntityDrag (form : NuEditForm) _ _ message world =
        match message.Data with
        | MouseButtonData (position, _) ->
            if form.interactButton.Checked then (message, true, world)
            else
                let editorState_ = world.ExtData :?> EditorState
                match editorState_.DragEntityState with
                | DragEntityNone -> (handle message, true, world)
                | DragEntityPosition _
                | DragEntityRotation _ ->
                    let editorState_ = { editorState_ with DragEntityState = DragEntityNone }
                    form.propertyGrid.Refresh ()
                    (handle message, true, { world with ExtData = editorState_ })
        | _ -> failwith <| "Expected MouseButtonData in message '" + str message + "'."

    let updateEntityDrag (form : NuEditForm) world =
        let editorState_ = world.ExtData :?> EditorState
        match editorState_.DragEntityState with
        | DragEntityNone -> world
        | DragEntityPosition (pickOffset, origMousePosition, address) ->
            let entityModel_ = get world <| worldEntityModelLens address
            let transform_ = getEntityModelTransform (Some world.Camera) (xdc world) entityModel_
            let transform_ = { transform_ with Position = (pickOffset - origMousePosition) + (world.MouseState.MousePosition - origMousePosition) }
            let (positionSnap, rotationSnap) = getSnaps form
            let entityModel_ = setEntityModelTransform (Some world.Camera) positionSnap rotationSnap transform_ (xdc world) entityModel_
            let world_ = set entityModel_ world <| worldEntityModelLens address
            let editorState_ = { editorState_ with DragEntityState = DragEntityPosition (pickOffset, origMousePosition, address) }
            let world_ = { world_ with ExtData = editorState_ }
            let world_ = propagateEntityModelPhysics address entityModel_ world_
            form.propertyGrid.Refresh ()
            world_
        | DragEntityRotation (pickOffset, origPosition, address) -> world

    let beginCameraDrag (form : NuEditForm) worldChangers refWorld _ _ message world =
        match message.Data with
        | MouseButtonData (position, _) ->
            if form.interactButton.Checked then (message, true, world)
            else
                let dragState = DragCameraPosition (world.Camera.EyePosition + world.MouseState.MousePosition, world.MouseState.MousePosition)
                let editorState_ = world.ExtData :?> EditorState
                let editorState_ = { editorState_ with DragCameraState = dragState }
                let world_ = { world with ExtData = editorState_ }
                (handle message, true, world_)
        | _ -> failwith <| "Expected MouseButtonData in message '" + str message + "'."

    let endCameraDrag (form : NuEditForm) _ _ message world =
        match message.Data with
        | MouseButtonData (position, _) ->
            if form.interactButton.Checked then (message, true, world)
            else
                let editorState_ = world.ExtData :?> EditorState
                match editorState_.DragCameraState with
                | DragCameraNone -> (handle message, true, world)
                | DragCameraPosition _ ->
                    let editorState_ = { editorState_ with DragCameraState = DragCameraNone }
                    (handle message, true, { world with ExtData = editorState_ })
        | _ -> failwith <| "Expected MouseButtonData in message '" + str message + "'."

    let updateCameraDrag (form : NuEditForm) world =
        let editorState_ = world.ExtData :?> EditorState
        match editorState_.DragCameraState with
        | DragCameraNone -> world
        | DragCameraPosition (pickOffset, origMousePosition) ->
            let eyePosition = (pickOffset - origMousePosition) + -1.0f * CameraSpeed * (world.MouseState.MousePosition - origMousePosition)
            let camera = { world.Camera with EyePosition = eyePosition }
            let world' = { world with Camera = camera }
            let editorState_ = { editorState_ with DragCameraState = DragCameraPosition (pickOffset, origMousePosition) }
            { world' with ExtData = editorState_ }

    /// Needed for physics system side-effects...
    let physicsHack world =
        let world' = { world with PhysicsMessages = ResetHackMessage :: world.PhysicsMessages }
        reregisterPhysicsHack EditorGroupAddress world'

    let handleExit (form : NuEditForm) _ =
        form.Close ()

    let handleCreate (form : NuEditForm) (worldChangers : WorldChanger List) refWorld atMouse _ =
        let changer = (fun world ->
            let pastWorld = world
            let entityPosition = if atMouse then world.MouseState.MousePosition else world.Camera.EyeSize * 0.5f
            let entityTransform = { Transform.Position = entityPosition; Depth = getCreationDepth form; Size = Vector2 DefaultEntitySize; Rotation = DefaultEntityRotation }
            let entityTypeName = typeof<EntityModel>.FullName + "+" + form.createEntityComboBox.Text
            let entityModel_ = makeDefaultEntityModel entityTypeName None
            let (positionSnap, rotationSnap) = getSnaps form
            let entityModel_ = setEntityModelTransform (Some world.Camera) positionSnap rotationSnap entityTransform (xdc world) entityModel_
            let entity = get entityModel_ entityLens
            let entityAddress = addrstr EditorGroupAddress entity.Name
            let world_ = addEntityModel entityAddress entityModel_ world
            let world_ = pushPastWorld pastWorld world_
            refWorld := world_ // must be set for property grid
            form.propertyGrid.SelectedObject <- { Address = entityAddress; Form = form; WorldChangers = worldChangers; RefWorld = refWorld }
            world_)
        refWorld := changer !refWorld
        worldChangers.Add changer

    let handleDelete (form : NuEditForm) (worldChangers : WorldChanger List) refWorld _ =
        let selectedObject = form.propertyGrid.SelectedObject
        let changer = (fun world ->
            match selectedObject with
            | :? EntityModelTypeDescriptorSource as entityModelTds ->
                let pastWorld = world
                let world_ = removeEntityModel entityModelTds.Address world
                let world_ = pushPastWorld pastWorld world_
                form.propertyGrid.SelectedObject <- null
                world_
            | _ -> world)
        refWorld := changer !refWorld
        worldChangers.Add changer

    let handleSave (form : NuEditForm) (worldChangers : WorldChanger List) refWorld _ =
        let saveFileResult = form.saveFileDialog.ShowDialog form
        match saveFileResult with
        | DialogResult.OK -> writeFile form.saveFileDialog.FileName !refWorld
        | _ -> ()

    let handleOpen (form : NuEditForm) (worldChangers : WorldChanger List) refWorld _ =
        let openFileResult = form.openFileDialog.ShowDialog form
        match openFileResult with
        | DialogResult.OK ->
            let changer = (fun world ->
                let world_ = loadFile form.openFileDialog.FileName world
                let world_ = clearPastWorlds world_
                form.propertyGrid.SelectedObject <- null
                form.interactButton.Checked <- false
                world_)
            refWorld := changer !refWorld
            worldChangers.Add changer
        | _ -> ()

    let handleUndo (form : NuEditForm) (worldChangers : WorldChanger List) refWorld _ =
        let changer = (fun world ->
            let futureWorld = world
            let editorState_ = world.ExtData :?> EditorState
            match editorState_.PastWorlds with
            | [] -> world
            | pastWorld :: pastWorlds ->
                let world_ = pastWorld
                let world_ = physicsHack world_
                let editorState_ = { editorState_ with PastWorlds = pastWorlds; FutureWorlds = futureWorld :: editorState_.FutureWorlds }
                let world_ = { world_ with ExtData = editorState_ }
                if form.interactButton.Checked then form.interactButton.Checked <- false
                form.propertyGrid.SelectedObject <- null
                world_)
        refWorld := changer !refWorld
        worldChangers.Add changer

    let handleRedo (form : NuEditForm) (worldChangers : WorldChanger List) refWorld _ =
        let changer = (fun world ->
            let pastWorld = world
            let editorState_ = world.ExtData :?> EditorState
            match editorState_.FutureWorlds with
            | [] -> world
            | futureWorld :: futureWorlds ->
                let world_ = futureWorld
                let world_ = physicsHack world_
                let editorState_ = { editorState_ with PastWorlds = pastWorld :: editorState_.PastWorlds; FutureWorlds = futureWorlds }
                let world_ = { world_ with ExtData = editorState_ }
                if form.interactButton.Checked then form.interactButton.Checked <- false
                form.propertyGrid.SelectedObject <- null
                world_)
        refWorld := changer !refWorld
        worldChangers.Add changer

    let handleInteractChanged (form : NuEditForm) (worldChangers : WorldChanger List) refWorld _ =
        if form.interactButton.Checked then
            let changer = (fun world -> pushPastWorld world world)
            refWorld := changer !refWorld
            worldChangers.Add changer

    let handleCut (form : NuEditForm) (worldChangers : WorldChanger List) refWorld _ =
        let optEntityModelTds = form.propertyGrid.SelectedObject
        match optEntityModelTds with
        | null -> ()
        | :? EntityModelTypeDescriptorSource as entityModelTds ->
            let changer = (fun world ->
                let pastWorld = world
                let editorState = world.ExtData :?> EditorState
                let entityModel = get world <| worldEntityModelLens entityModelTds.Address
                let world' = removeEntityModel entityModelTds.Address world
                editorState.Clipboard := Some entityModel
                form.propertyGrid.SelectedObject <- null
                world')
            refWorld := changer !refWorld
            worldChangers.Add changer
        | _ -> trace <| "Invalid cut operation (likely a code issue in NuEdit)."
        
    let handleCopy (form : NuEditForm) (worldChangers : WorldChanger List) refWorld _ =
        let optEntityModelTds = form.propertyGrid.SelectedObject
        match optEntityModelTds with
        | null -> ()
        | :? EntityModelTypeDescriptorSource as entityModelTds ->
            let entityModel = get !refWorld <| worldEntityModelLens entityModelTds.Address
            let editorState = (!refWorld).ExtData :?> EditorState
            editorState.Clipboard := Some entityModel
        | _ -> trace <| "Invalid copy operation (likely a code note in NuEdit)."

    let handlePaste (form : NuEditForm) (worldChangers : WorldChanger List) refWorld atMouse _ =
        let editorState = (!refWorld).ExtData :?> EditorState
        match !editorState.Clipboard with
        | None -> ()
        | Some entityModel_ ->
            let changer = (fun world ->
                let entity_ = get entityModel_ entityLens
                let id = getNuId ()
                let entity_ = { entity_ with Id = id; Name = str id }
                let entityModel_ = set entity_ entityModel_ entityLens
                let entityPosition = if atMouse then world.MouseState.MousePosition else world.Camera.EyeSize * 0.5f
                let entityTransform_ = getEntityModelTransform (Some world.Camera) (xdc world) entityModel_
                let entityTransform_ = { entityTransform_ with Position = entityPosition; Depth = getCreationDepth form }
                let (positionSnap, rotationSnap) = getSnaps form
                let entityModel_ = setEntityModelTransform (Some world.Camera) positionSnap rotationSnap entityTransform_ (xdc world) entityModel_
                let address = addrstr EditorGroupAddress entity_.Name
                let pastWorld = world
                let world_ = pushPastWorld pastWorld world
                addEntityModel address entityModel_ world_)
            refWorld := changer !refWorld
            worldChangers.Add changer

    // TODO: add undo to quick size
    let handleQuickSize (form : NuEditForm) (worldChangers : WorldChanger List) refWorld _ =
        let optEntityModelTds = form.propertyGrid.SelectedObject
        match optEntityModelTds with
        | null -> ()
        | :? EntityModelTypeDescriptorSource as entityModelTds ->
            let changer = (fun world ->
                let entityModel_ = get world <| worldEntityModelLens entityModelTds.Address
                let entityQuickSize = getEntityModelQuickSize world.AssetMetadataMap (xdc world) entityModel_
                let entityTransform = { getEntityModelTransform None (xdc world) entityModel_ with Size = entityQuickSize }
                let entityModel_ = setEntityModelTransform None 0 0 entityTransform (xdc world) entityModel_
                let world_ = set entityModel_ world <| worldEntityModelLens entityModelTds.Address
                refWorld := world_ // must be set for property grid
                form.propertyGrid.Refresh ()
                world_)
            refWorld := changer !refWorld
            worldChangers.Add changer
        | _ -> trace <| "Invalid quick size operation (likely a code issue in NuEdit)."

    let handleResetCamera (form : NuEditForm) (worldChangers : WorldChanger List) refWorld _ =
        let changer = (fun world ->
            let camera = { world.Camera with EyePosition = Vector2.Zero }
            { world with Camera = camera })
        refWorld := changer !refWorld
        worldChangers.Add changer

    let handleAddXField (form : NuEditForm) (worldChangers : WorldChanger List) refWorld _ =
        match (form.xFieldNameTextBox.Text, form.typeNameTextBox.Text) with
        | ("", _) -> ignore <| MessageBox.Show "Enter an XField name."
        | (_, "") -> ignore <| MessageBox.Show "Enter a type name."
        | (xFieldName, typeName) ->
            match tryFindType typeName with
            | null -> ignore <| MessageBox.Show "Enter a valid type name."
            | aType ->
                let selectedObject = form.propertyGrid.SelectedObject
                match selectedObject with
                | :? EntityModelTypeDescriptorSource as entityModelTds ->
                    let changer = (fun world ->
                        let pastWorld = world
                        let entityModel = get world <| worldEntityModelLens entityModelTds.Address
                        let entity = get entityModel entityLens
                        let xFieldValue = if aType = typeof<string> then String.Empty :> obj else Activator.CreateInstance aType
                        let xFieldLun = Lun.make xFieldName
                        let xFields = Map.add xFieldLun xFieldValue entity.Xtension.XFields
                        let entity' = { entity with Xtension = { entity.Xtension with XFields = xFields }}
                        let world' = set entity' world <| worldEntityLens entityModelTds.Address
                        let world'' = pushPastWorld pastWorld world'
                        refWorld := world'' // must be set for property grid
                        form.propertyGrid.Refresh ()
                        form.propertyGrid.Select ()
                        form.propertyGrid.SelectedGridItem <- form.propertyGrid.SelectedGridItem.Parent.GridItems.[xFieldName]
                        world'')
                    refWorld := changer !refWorld
                    worldChangers.Add changer
                | _ -> ignore <| MessageBox.Show "Select an entity to add the XField to."

    let handleRemoveSelectedXField (form : NuEditForm) (worldChangers : WorldChanger List) refWorld _ =
        let selectedObject = form.propertyGrid.SelectedObject
        match selectedObject with
        | :? EntityModelTypeDescriptorSource as entityModelTds ->
            match form.propertyGrid.SelectedGridItem.Label with
            | "" -> ignore <| MessageBox.Show "Select an XField."
            | xFieldName ->
                let changer = (fun world ->
                    let pastWorld = world
                    let entityModel = get world <| worldEntityModelLens entityModelTds.Address
                    let entity = get entityModel entityLens
                    let xFieldLun = Lun.make xFieldName
                    let xFields = Map.remove xFieldLun entity.Xtension.XFields
                    let entity' = { entity with Xtension = { entity.Xtension with XFields = xFields }}
                    let world' = set entity' world <| worldEntityLens entityModelTds.Address
                    let world'' = pushPastWorld pastWorld world'
                    refWorld := world'' // must be set for property grid
                    form.propertyGrid.Refresh ()
                    world'')
                refWorld := changer !refWorld
                worldChangers.Add changer
        | _ -> ignore <| MessageBox.Show "Select an entity to remove an XField from."

    let handleClearAllXFields (form : NuEditForm) (worldChangers : WorldChanger List) refWorld _ =
        let selectedObject = form.propertyGrid.SelectedObject
        match selectedObject with
        | :? EntityModelTypeDescriptorSource as entityModelTds ->
            let changer = (fun world ->
                let pastWorld = world
                let entityModel = get world <| worldEntityModelLens entityModelTds.Address
                let entity = get entityModel entityLens
                let entity' = { entity with Xtension = { entity.Xtension with XFields = Map.empty }}
                let world' = set entity' world <| worldEntityLens entityModelTds.Address
                let world'' = pushPastWorld pastWorld world'
                refWorld := world'' // must be set for property grid
                form.propertyGrid.Refresh ()
                world'')
            refWorld := changer !refWorld
            worldChangers.Add changer
        | _ -> ignore <| MessageBox.Show "Select an entity to clear all XFields from."
        
    let createNuEditForm worldChangers refWorld =
        let form = new NuEditForm ()
        form.displayPanel.MaximumSize <- Drawing.Size (VirtualResolutionX, VirtualResolutionY)
        form.positionSnapTextBox.Text <- str DefaultPositionSnap
        form.rotationSnapTextBox.Text <- str DefaultRotationSnap
        form.creationDepthTextBox.Text <- str DefaultCreationDepth
        for unionCase in FSharpType.GetUnionCases typeof<EntityModel> do
            ignore <| form.createEntityComboBox.Items.Add unionCase.Name
        form.createEntityComboBox.SelectedIndex <- 0
        form.exitToolStripMenuItem.Click.Add (handleExit form)
        form.createEntityButton.Click.Add (handleCreate form worldChangers refWorld false)
        form.createToolStripMenuItem.Click.Add (handleCreate form worldChangers refWorld false)
        form.createContextMenuItem.Click.Add (handleCreate form worldChangers refWorld true)
        form.deleteEntityButton.Click.Add (handleDelete form worldChangers refWorld)
        form.deleteToolStripMenuItem.Click.Add (handleDelete form worldChangers refWorld)
        form.deleteContextMenuItem.Click.Add (handleDelete form worldChangers refWorld)
        form.saveToolStripMenuItem.Click.Add (handleSave form worldChangers refWorld)
        form.openToolStripMenuItem.Click.Add (handleOpen form worldChangers refWorld)
        form.undoButton.Click.Add (handleUndo form worldChangers refWorld)
        form.undoToolStripMenuItem.Click.Add (handleUndo form worldChangers refWorld)
        form.redoButton.Click.Add (handleRedo form worldChangers refWorld)
        form.redoToolStripMenuItem.Click.Add (handleRedo form worldChangers refWorld)
        form.interactButton.CheckedChanged.Add (handleInteractChanged form worldChangers refWorld)
        form.cutToolStripMenuItem.Click.Add (handleCut form worldChangers refWorld)
        form.cutContextMenuItem.Click.Add (handleCut form worldChangers refWorld)
        form.copyToolStripMenuItem.Click.Add (handleCopy form worldChangers refWorld)
        form.copyContextMenuItem.Click.Add (handleCopy form worldChangers refWorld)
        form.pasteToolStripMenuItem.Click.Add (handlePaste form worldChangers refWorld false)
        form.pasteContextMenuItem.Click.Add (handlePaste form worldChangers refWorld true)
        form.quickSizeToolStripButton.Click.Add (handleQuickSize form worldChangers refWorld)
        form.resetCameraButton.Click.Add (handleResetCamera form worldChangers refWorld)
        form.addXFieldButton.Click.Add (handleAddXField form worldChangers refWorld)
        form.removeSelectedXFieldButton.Click.Add (handleRemoveSelectedXField form worldChangers refWorld)
        form.clearAllXFieldsButton.Click.Add (handleClearAllXFields form worldChangers refWorld)
        form.Show ()
        form

    let tryCreateEditorWorld form worldChangers refWorld sdlDeps =
        let screen = makeDissolveScreen 100 100
        let group = makeDefaultGroup ()
        let editorState = { DragEntityState = DragEntityNone; DragCameraState = DragCameraNone; PastWorlds = []; FutureWorlds = []; Clipboard = ref None }
        let optWorld = tryCreateEmptyWorld sdlDeps editorState
        match optWorld with
        | Left errorMsg -> Left errorMsg
        | Right world ->
            refWorld := world
            refWorld := addScreen EditorScreenAddress screen [(EditorGroupName, group, [])] !refWorld
            refWorld := set (Some EditorScreenAddress) !refWorld worldOptSelectedScreenAddressLens
            refWorld := subscribe DownMouseLeftAddress [] (beginEntityDrag form worldChangers refWorld) !refWorld
            refWorld := subscribe UpMouseLeftAddress [] (endEntityDrag form) !refWorld
            refWorld := subscribe DownMouseCenterAddress [] (beginCameraDrag form worldChangers refWorld) !refWorld
            refWorld := subscribe UpMouseCenterAddress [] (endCameraDrag form) !refWorld
            Right !refWorld

    // TODO: remove code duplication with below
    let updateUndo (form : NuEditForm) world =
        let editorState = world.ExtData :?> EditorState
        if form.undoToolStripMenuItem.Enabled then
            if List.isEmpty editorState.PastWorlds then
                form.undoButton.Enabled <- false
                form.undoToolStripMenuItem.Enabled <- false
        elif not <| List.isEmpty editorState.PastWorlds then
            form.undoButton.Enabled <- true
            form.undoToolStripMenuItem.Enabled <- true

    let updateRedo (form : NuEditForm) world =
        let editorState = world.ExtData :?> EditorState
        if form.redoToolStripMenuItem.Enabled then
            if List.isEmpty editorState.FutureWorlds then
                form.redoButton.Enabled <- false
                form.redoToolStripMenuItem.Enabled <- false
        elif not <| List.isEmpty editorState.FutureWorlds then
            form.redoButton.Enabled <- true
            form.redoToolStripMenuItem.Enabled <- true

    let updateEditorWorld form (worldChangers : WorldChangers) refWorld world =
        refWorld := snd <| updateTransition (fun world2 -> true, world2) world 
        refWorld := updateEntityDrag form !refWorld
        refWorld := updateCameraDrag form !refWorld
        refWorld := Seq.fold (fun world' changer -> changer world') !refWorld worldChangers
        worldChangers.Clear ()
        let editorState = (!refWorld).ExtData :?> EditorState
        updateUndo form !refWorld
        updateRedo form !refWorld
        (not form.IsDisposed, !refWorld)

    let [<EntryPoint; STAThread>] main _ =
        initTypeConverters ()
        let worldChangers = WorldChangers ()
        let refWorld = ref Unchecked.defaultof<World>
        use form = createNuEditForm worldChangers refWorld
        let sdlViewConfig = ExistingWindow form.displayPanel.Handle
        let sdlRenderFlags = enum<SDL.SDL_RendererFlags> (int SDL.SDL_RendererFlags.SDL_RENDERER_ACCELERATED ||| int SDL.SDL_RendererFlags.SDL_RENDERER_PRESENTVSYNC)
        let sdlConfig = makeSdlConfig sdlViewConfig form.displayPanel.MaximumSize.Width form.displayPanel.MaximumSize.Height sdlRenderFlags 1024
        run4
            (tryCreateEditorWorld form worldChangers refWorld)
            (updateEditorWorld form worldChangers refWorld)
            (fun world -> form.displayPanel.Invalidate (); world)
            sdlConfig