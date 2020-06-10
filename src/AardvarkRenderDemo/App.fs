namespace SLEAardvarkRenderDemo

open System
open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.SceneGraph
open Aardvark.SceneGraph.IO
open Aardvark.SceneGraph.Semantics
open Aardvark.UI
open Aardvark.UI.Primitives
open Aardvark.Base.Rendering
open SLEAardvarkRenderDemo.Model
open Aardvark.Base.Ag
open material
open Aardvark.SceneGraph.IO.Loader.SgSems

(*
   The main addaptive elm-style App and the main setup of the render pipeline
*)
//the main app message types
type Message =
    | CameraMessage of FreeFlyController.Message
    | LightMessage of int * lightControl.Message //from light.fs
    | RemoveLight  of int
    | AddLight of Light
    | SetExpousure of float
    | BloomMessage of BloomControl.Message
    | GlobalEnviormentMessage of globalEnviroment.Message // from globalEnviroment.fs
    | SceneObjectMessage of sceneObjectControl.Message //from  SceneObject.fs
    | AddSceneObject of string
    | RemoveSceneObject of string
    | SelectObject of string
    | SaveProject of string
    | LoadProject of string
    | FxAAMessage  of fxAA.Message

module App =   

    //setup the initial state
    let cameraConfig  =  {FreeFlyController.initial.freeFlyConfig with zoomMouseWheelSensitivity = 0.5} 
    let initialView = CameraView.lookAt (V3d(0.0, 2.0, -6.0)) (V3d(0.0, 2.0, 0.0)) (V3d.OIO * 1.0)

    let (!!) inner = 
        Path.combine ([__SOURCE_DIRECTORY__;"..";"..";"data";] @ inner)

        

    let obj , selected = sceneObject.loadObject HashMap.empty !!["SLE_Gnom4.obj"]

    let defaultModel = { 
        cameraState = {FreeFlyController.initial  with freeFlyConfig = cameraConfig; view = initialView}
        lights = HashMap.ofList [(0, light.defaultDirectionalLight)]
        enviorment = {skyMap = !!["GrandCanyon_C_YumaPoint";"GCanyon_C_YumaPoint_3k.hdr"]
                      skyMapRotation = Math.PI; 
                      skyMapIntensity = 1.0;
                      ambientLightIntensity = 1.0
                      occlusionSettings = light.defaultAbientOcclusion
                      lightProbePosition = None
                      }
        expousure  = 1.0
        fxAA = fxAA.defaultfxAA
        bloom = bloom.defaultBloom
        objects = obj
        selectedObject = selected
    }

    let initial = 
        let file = !!["initial_scene.json"]
        if (System.IO.File.Exists file)
        then 
           projetIO.load file
        else defaultModel

    //the main update function
    let update (m : Model) (msg : Message) =
        //compose the update function from the updates of the sub-model
        match msg with
        | CameraMessage msg ->
            { m with cameraState = FreeFlyController.update m.cameraState msg }
        | LightMessage (i, lms) -> 
            let li' = HashMap.tryFind i  m.lights //from light.fs
            match  li' with 
            |Some li ->
                let l = lightControl.update li lms
                { m with lights = HashMap.update i (fun _ -> l ) m.lights }
            |None ->  m
        | RemoveLight i ->  { m with lights = HashMap.remove i m.lights }
        | AddLight l -> 
            let i = 
                if HashMap.isEmpty m.lights then
                    1
                else
                    HashMap.keys m.lights
                    |> Seq.max
                    |> max 0
                    |> (+) 1
            { m with lights = HashMap.add i l m.lights }
        | SceneObjectMessage (msg) ->
            let o' = HashMap.tryFind m.selectedObject m.objects
            match o' with
            |Some o  ->
                let newo = sceneObjectControl.update o msg //from  SceneObject.fs
                let objects = HashMap.update m.selectedObject (fun _ -> newo ) m.objects
                { m with objects = objects }
            |None -> m
        | RemoveSceneObject name ->
            let obj =  HashMap.remove name m.objects  
            let selected =  obj |> HashMap.keys |> Seq.first |> Option.defaultValue "None"
            { m with objects = obj; selectedObject = selected }           
        | AddSceneObject file ->
            let objects, selected = sceneObject.loadObject m.objects file
            { m with objects = objects; selectedObject = selected}
        | SelectObject name -> { m with selectedObject = name }
        | SetExpousure e ->
            { m with expousure = e }
        | BloomMessage msg ->  
             { m with bloom = BloomControl.update m.bloom msg }
        | GlobalEnviormentMessage msg ->
            { m with enviorment = globalEnviroment.update m.enviorment msg } // from globalEnviroment.fs
        | SaveProject f -> 
            Log.warn "Save %s" f
            do projetIO.save m f
            m
        | LoadProject f -> projetIO.load f
        | FxAAMessage msg -> 
            { m with fxAA = fxAA.update m.fxAA msg }

     //texture  with random values used in the AO shaders
    let randomTex ( runtime : IRuntime) = 
        let img = PixImage<float32>(Col.Format.RGB, V2i.II * 512)

        let rand = RandomSystem()
        img.GetMatrix<C3f>().SetByCoord (fun _ ->
            rand.UniformV3dDirection().ToC3d().ToC3f()
        ) |> ignore

        runtime.PrepareTexture(PixTexture2d(PixImageMipMap [| img :> PixImage |], TextureParams.empty))

    //Render-Task for the screen-space Abient Occlusion pass
    let makeAmbientOcclusion ( runtime : IRuntime) (size : aval<V2i>) view proj gBuffer (settings : AdaptiveAmbientOcclusionSettings)=

        let signature =
            runtime.CreateFramebufferSignature [
                DefaultSemantic.Colors, RenderbufferFormat.Rgba8
            ]

        let aoSize = 
             AVal.custom (fun t ->
                let s = size.GetValue t
                let d = settings.scale.GetValue t
                V2i(
                    max 1 (int (float s.X * d)),
                    max 1 (int (float s.Y * d))
                )
            )

        let ambientOc = 
            Sg.fullScreenQuad
            |> Sg.adapter
            |> Sg.shader {
                do! SSAO.ambientOcclusion
            }
            |> Sg.texture ( DefaultSemantic.Normals) (Map.find DefaultSemantic.Normals gBuffer)
            |> Sg.texture ( DefaultSemantic.Depth) (Map.find DefaultSemantic.Depth gBuffer)
            |> Sg.viewTrafo view
            |> Sg.projTrafo proj
            |> Sg.uniform "Random" (AVal.constant (randomTex runtime :> ITexture))
           
            |> Sg.uniform "Radius" settings.radius
            |> Sg.uniform "Threshold" settings.threshold
            |> Sg.uniform "Samples" settings.samples
            |> Sg.uniform "OcclusionStrength" settings.occlusionStrength
            |> Sg.compile runtime signature
            |> RenderTask.renderToColor aoSize

        let blurredAmbientOc =
            Sg.fullScreenQuad
            |> Sg.adapter
            |> Sg.shader {
                do! SSAO.blur
            }
            |> Sg.texture ( DefaultSemantic.Depth) (Map.find DefaultSemantic.Depth gBuffer)
            |> Sg.texture (Sym.ofString "AmbientOcclusion") ambientOc
            |> Sg.viewTrafo view
            |> Sg.projTrafo proj
            |> Sg.uniform "Radius" settings.radius
            |> Sg.uniform "Threshold" settings.threshold
            |> Sg.uniform "Sigma" settings.sigma
            |> Sg.uniform "Sharpness" settings.sharpness
            |> Sg.compile runtime signature
            |> RenderTask.renderToColor aoSize

        blurredAmbientOc

    //main render task: put all passes together for deferred rendering
    let compileDeffered (scene : ISg<'msg>) (m : AdaptiveModel) (values : Aardvark.Service.ClientValues)=
        let outputSignature = values.signature
        let view = values.viewTrafo
        let proj = values.projTrafo
        let size = values.size |> AVal.map (fun s -> V2i(max 1 s.X, max 1 s.Y))
        let runtime = outputSignature.Runtime

        //render the speical sky map to a texture cube
        //Ardvark will only rexecute this if the skyMap or rotation changes
        let skyBoxTexture = SkyBox.getTexture runtime m.enviorment.skyMap m.enviorment.skyMapRotation

        //calculate the  global bounding box
        let bb = //scene?GlobalBoundingBox() //not defined for Sg.set Todo: define semantics
            let seed = Box3d(V3d.OOO, V3d.OOO) |> AVal.constant
            let bounds (o : AdaptiveSceneObject) =
                adaptive{
                    let! object  = sceneObject.object o
                    let! trans = sceneObject.trafo o
                    let bounds = object.bounds
                    return bounds.Transformed(trans)
                }    
            m.objects
            |> AMap.toASet       
            |> ASet.fold (fun s (_,(o : AdaptiveSceneObject)) -> AVal.map2( fun (s : Box3d) (b : Box3d)-> Box.Union(s,b)) s (bounds o)) seed 
            |> AVal.bind id

        //adaptive function to calcualte the light view matrix for one light
        let lightViewMatrix i = 
            let light = AMap.find i m.lights
            AVal.bind (Shadow.lightViewPoject bb) light

        //adaptive function to calcualte the shadow map for one light
        let shadowMapTex i = 
            let light = AMap.find i m.lights
            AVal.bind (Shadow.shadowMap runtime scene bb) light 

        // lightning pass per light
        let lightSgs0 = 
            let lightSet =
                m.lights
                |> AMap.toASet
            aset  {
                for  (i,l) in lightSet do
                    let l' = l
                    let pass = 
                        match l' with 
                        |AdaptiveDirectionalLight dl ->
                            AVal.map (fun (d : DirectionalLightData)->
                                if d.castsShadow then
                                    Sg.fullScreenQuad
                                    |> Sg.adapter
                                    |> Sg.uniform "Light" (SLEUniform.uniformLight l)
                                    |> Sg.texture (Sym.ofString "ShadowMap") (shadowMapTex i)
                                    |> Sg.uniform "LightViewMatrix" (lightViewMatrix  i |> AVal.map(fun (v,p)  -> v * p))
                                    |> Sg.shader {
                                        do! PBR.getGBufferData
                                        do! PBR.lightingDeferred
                                        do! PBR.shadowDeferred
                                        }
                                else
                                     Sg.fullScreenQuad
                                    |> Sg.adapter
                                    |> Sg.uniform "Light" (SLEUniform.uniformLight l)
                                    |> Sg.shader {
                                        do! PBR.getGBufferData
                                        do! PBR.lightingDeferred
                                        } ) dl
                            |> Sg.dynamic                                
                        |AdaptiveSpotLight sl ->
                            AVal.map (fun (d : SpotLightData)->
                                if d.castsShadow then
                                    Sg.fullScreenQuad
                                    |> Sg.adapter
                                    |> Sg.uniform "Light" (SLEUniform.uniformLight l)
                                    |> Sg.texture (Sym.ofString "ShadowMap") (shadowMapTex i)
                                    |> Sg.uniform "LightViewMatrix" (lightViewMatrix  i |> AVal.map(fun (v,p)  -> v * p))
                                    |> Sg.shader {
                                        do! PBR.getGBufferData
                                        do! PBR.lightingDeferred
                                        do! PBR.shadowDeferred
                                        }
                                else
                                     Sg.fullScreenQuad
                                    |> Sg.adapter
                                    |> Sg.uniform "Light" (SLEUniform.uniformLight l)
                                    |> Sg.shader {
                                        do! PBR.getGBufferData
                                        do! PBR.lightingDeferred
                                        } ) sl
                            |> Sg.dynamic                          
                        |AdaptivePointLight _ ->
                            Sg.fullScreenQuad
                            |> Sg.adapter
                            |> Sg.uniform "Light" (SLEUniform.uniformLight l)
                            |> Sg.shader {
                                do! PBR.getGBufferData
                                do! PBR.lightingDeferred
                                }
                    yield  pass
            }

        let enviromentTexture = 
            m.enviorment.lightProbePosition
            |> AVal.bind 
                (fun (p' : V3d option) -> 
                    match p' with
                    |Some p -> LightProbe.lightProbe runtime scene lightSgs0 skyBoxTexture m.enviorment.skyMapIntensity m.enviorment.ambientLightIntensity p :> aval<ITexture>
                    |None -> skyBoxTexture :> aval<ITexture>
                )

        //render the precalculated  Maps for PBR Global Ambient Light
        //Ardvark will only rexecute this if the global enviroment changes
        let diffuseIrradianceMap = GlobalAmbientLight.diffuseIrradianceMap runtime enviromentTexture

        let prefilterdSpecColor = GlobalAmbientLight.prefilterdSpecColor runtime enviromentTexture

        let bRDFLtu = GlobalAmbientLight.BRDFLtu runtime

        //render the geometry to the gBuffer
        let gBuffer = GeometryBuffer.makeGBuffer runtime view proj size skyBoxTexture scene m.enviorment.skyMapIntensity

        //render the abient occlousion map    
        let ambientOcclusion = makeAmbientOcclusion runtime size view proj gBuffer m.enviorment.occlusionSettings

        let lightSgs =
            aset {
                yield! lightSgs0
                let pass0 = //global  abient  lightnig
                    Sg.fullScreenQuad
                    |> Sg.adapter
                    |> Sg.texture (Sym.ofString "DiffuseIrradiance") diffuseIrradianceMap
                    |> Sg.texture (Sym.ofString "PrefilteredSpecColor") prefilterdSpecColor
                    |> Sg.texture (Sym.ofString "BRDFLtu") bRDFLtu
                    |> Sg.texture (Sym.ofString "AmbientOcclusion") ambientOcclusion
                    |> Sg.shader {
                        do! PBR.getGBufferData
                        do! PBR.abientDeferred
                        do! PBR.nonLightedDeferred
                        }
                yield pass0
           } 
            
        //additive blending
        let mutable blendMode = BlendMode(true)
        blendMode.AlphaOperation <- BlendOperation.Add
        blendMode.Operation <- BlendOperation.Add
        blendMode.SourceFactor <- BlendFactor.One
        blendMode.SourceAlphaFactor <- BlendFactor.One
        blendMode.DestinationFactor <- BlendFactor.One
        blendMode.DestinationAlphaFactor <- BlendFactor.One

        let signature =
            runtime.CreateFramebufferSignature [
                DefaultSemantic.Colors, RenderbufferFormat.Rgba32f
            ]

        //render linear HDR output
        let  output = 
            Sg.set lightSgs
            |> Sg.blendMode (blendMode |> AVal.constant)
            |> Sg.uniform "AmbientIntensity" m.enviorment.ambientLightIntensity
            |> Sg.uniform "CameraLocation" (view |> AVal.map (fun t -> t.Backward.C3.XYZ))
            |> Sg.texture ( DefaultSemantic.Colors) (Map.find DefaultSemantic.Colors gBuffer)
            |> Sg.texture ( Sym.ofString "WPos") (Map.find (Sym.ofString "WorldPosition") gBuffer)
            |> Sg.texture ( DefaultSemantic.Normals) (Map.find DefaultSemantic.Normals gBuffer)
            |> Sg.texture ( DefaultSemantic.Depth) (Map.find DefaultSemantic.Depth gBuffer)
            |> Sg.texture (GBufferRendering.Semantic.Emission) (Map.find GBufferRendering.Semantic.Emission gBuffer)
            |> Sg.texture ( GBufferRendering.Semantic.MaterialProperties) (Map.find GBufferRendering.Semantic.MaterialProperties gBuffer)
            |> Sg.compile runtime signature
            |> RenderTask.renderToColor size   

        let bloomed = 
            AVal.bind (fun doBloom  ->  
                if doBloom then
                    bloom.bloom runtime size output m.bloom :> aval<ITexture>
                else 
                    output :> aval<ITexture>) 
                m.bloom.on
        
        let postprocessed = 
            AVal.bind (fun doAA  ->  
                if doAA then  
                    fxAA.fxAA runtime size signature bloomed m.fxAA :> aval<ITexture>
                else 
                    bloomed) 
                m.fxAA.on

        //tone mapping and gamma correction
        //let toneMapped =
        Sg.fullScreenQuad
        |> Sg.adapter
        |> Sg.texture DefaultSemantic.DiffuseColorTexture postprocessed
        |> Sg.uniform "Expousure" m.expousure
        |> Sg.shader {
            do! DefaultSurfaces.diffuseTexture
            do! PBR.gammaCorrection
            }    
        |> Sg.compile runtime outputSignature 
               

        

        
    (*
        For deferrde Rendering and some other techniques it is nessesary to know the size of the render window.
        That is not straitforward in Aardvark.media because there could be multiple clients with different screen sizes.
        The solution is to create a custem scene that takes a function from client values to a IRenderTask and feed that into a RenderControl.
        If I understand it correctly, at runtime a render Task per client will be created with the respectice client values.
        Thanks to Georg Haaser for the tip. 
        Note that the ClientValues contain the Output Framebuffer Signature. That is very usefull because you can obtain a reference to the runtime from the signatur. 
    *)
    let RenderControl (att : list<string * AttributeValue<Message>>) (s : AdaptiveCameraControllerState) (frustum : Frustum) (task : Aardvark.Service.ClientValues -> IRenderTask) =
        let scene =  Aardvark.Service.Scene.custom task
        let cam : aval<Camera> = AVal.map (fun v -> { cameraView = v; frustum = frustum }) s.view 
        DomNode.RenderControl(AttributeMap.ofList att, cam, scene, None)
        |> FreeFlyController.withControls s CameraMessage (AVal.constant frustum)

    //the 3D scene and control
    let view3D (m : AdaptiveModel) =

        let frustum = 
            Frustum.perspective 30.0 0.1 100.0 1.0 

        //generate sceen graph from the objects 
        let objects = 
            aset {
                for _,o in AMap.toASet m.objects do
                    let! s = sceneObject.sg o
                    yield s |> Sg.trafo (sceneObject.trafo o ) 
            }
            |> Sg.set  

        let att =
            [
                style "position: fixed; left: 0; top: 0; width: 100%; height: 100%"
                attribute "showFPS" "true"
                //attribute "data-renderalways" "1"
            ]

        let t = compileDeffered objects m
        RenderControl att m.cameraState frustum t

    // main view for UI and  
    let view (m : AdaptiveModel) =
        let saveButton = 
            openDialogButton 
                { OpenDialogConfig.file with allowMultiple = false; title = "Save"; filters  = [|"*.*"|];  startPath = ""; mode  = OpenDialogMode.File}
                [ clazz "ui green button"; onChooseFile SaveProject ] 
                [ text "Save" ]
        let loadButton = 
            openDialogButton 
                { OpenDialogConfig.file with allowMultiple = false; title = "load"; filters  = [|"*.*"|];  startPath = ""; mode  = OpenDialogMode.File}
                [ clazz "ui green button"; onChooseFile LoadProject ] 
                [ text "Load" ]
        require Html.semui ( 
            body [] (        
                Html.SemUi.adornerAccordeonMenu [ 
                "Edit Objects",
                    [
                        Html.table [                        
                            tr [] [ td [attribute "colspan" "2"] [
                                openDialogButton 
                                 { OpenDialogConfig.file with allowMultiple = false; title = "Open Model file"; filters  = [|"*.obj"|];  startPath = ""; mode  = OpenDialogMode.File}
                                 [ clazz "ui green button"; onChooseFile AddSceneObject ] 
                                 [ text "Add Object" ]
                                ]]
                            tr [] [ td [] [text "Object"]; td [style "width: 70%;"] [Html.SemUi.dropDown' (m.objects |> AMap.keys |> ASet.toAList) m.selectedObject SelectObject id]]
                            tr [] [ td [attribute "colspan" "2"] [
                                button [clazz "ui button"; onClick (fun _ -> RemoveSceneObject (AVal.force m.selectedObject))]  [text "Remove selected Object"]
                                ]]
                        ]
                        m.selectedObject
                        |> AVal.bind (fun n -> AMap.tryFind n m.objects)
                        |> AVal.map (Option.map  (sceneObjectControl.view >> UI.map SceneObjectMessage  ) >> IndexList.single)
                        |> AList.ofAVal
                        |> AList.choose id
                        |> Incremental.div AttributeMap.empty
                    ]    
                "Add Light",
                    [
                        button [clazz "ui button"; onClick (fun _ -> AddLight light.defaultDirectionalLight); style "margin-bottom: 5px; width: 100%;" ]  [text "Directional Light"]
                        button [clazz "ui button"; onClick (fun _ -> AddLight light.defaultPointLight); style "margin-bottom: 5px; width: 100%;" ]  [text "Point Light"]
                        button [clazz "ui button"; onClick (fun _ -> AddLight light.defaultSpotLight); style "margin-bottom: 5px; width: 100%;" ]  [text "Spot Light"]
                    ]    
                "Change Light",
                    [   //build a list of light views from the set of lights
                        m.lights
                        |> AMap.fold 
                            ( fun items i l -> 
                                let name = 
                                    match l with
                                    |AdaptiveDirectionalLight _ -> sprintf "Directional Light %i" i
                                    |AdaptivePointLight _ -> sprintf "Point Light %i" i
                                    |AdaptiveSpotLight _ -> sprintf "Spot Light %i" i
                                let d = 
                                        lightControl.view  l |> UI.map (fun msg -> LightMessage (i, msg)) 
                                        |> AList.single
                                        |> Incremental.div AttributeMap.empty
                                
                                let item = 
                                    name, [
                                        d
                                        button [clazz "ui button"; onClick (fun _ -> RemoveLight i); style "margin-top: 5px;width: 100%;" ]  [text "Remove"]
                                        ]
                                item::items
                            ) []
                         //feed that into a accordeon
                        |> AVal.map (Html.SemUi.accordionMenu true "ui vertical inverted fluid accordion menu" >> IndexList.single)
                        // and that  into  a incremantal div to handel the case that the numbers of lights change
                        |> AList.ofAVal
                        |> Incremental.div AttributeMap.empty
                    ]
                "Global Enviorment",
                    [
                        globalEnviroment.view m.enviorment |> UI.map GlobalEnviormentMessage
                    ]  
                "Render Settings",
                    [
                        Html.table [                        
                            tr [] [ td [] [text "Exposure"]; td [ style "width: 70%;"] [inputLogSlider {min = 0.01;  max = 10.0; step = 0.01} [] m.expousure SetExpousure]]
                        ]  
                        BloomControl.view m.bloom |>  UI.map BloomMessage 
                        fxAA.view m.fxAA |>  UI.map FxAAMessage 
                    ]    
                "Project",
                    [
                        saveButton
                        loadButton
                    ]  
                ] [view3D  m]
            )
        )

    //assmble the map
    let app =
        {
            initial = initial
            update = update
            view = view
            threads = fun _ -> ThreadPool.empty
            unpersist = Unpersist.instance
        }