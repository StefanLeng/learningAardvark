namespace SLEAardvarkRenderDemo

open Aardvark.Base
open Aardvark.Base.Rendering
open FShade
open Aardvark.Base.Rendering.Effects
open System
(*
    Shaders for deffered Rendering, pysical based rendering (PBR) and screen space abient occlusion (SSAO)

    They are mostly ports of the tutorials found at https://learnopengl.com/https://learnopengl.com/ 
*)


//physical base rendering, mostly a port of the shaders found here https://learnopengl.com/PBR/Lighting,
//here https://learnopengl.com/PBR/IBL/Diffuse-irradiance and here https://learnopengl.com/PBR/IBL/Specular-IBL.

module PBR =
    open fshadeExt
    open BRDF
    open GBuffer
    
    type UniformScope with
        member x.Light : SLEUniform.Light = x?Light
        member x.AmbientIntensity : float = x?AmbientIntensity

    //Note: Do not use ' in variabel names for shader code, it will lead to an error

    let private diffuseIrradianceSampler =
        samplerCube {
            texture uniform?DiffuseIrradiance
            filter Filter.MinMagMipLinear
            addressU WrapMode.Wrap
            addressV WrapMode.Wrap
        }

    let private prefilteredSpecColorSampler =
        samplerCube {
            texture uniform?PrefilteredSpecColor
            filter Filter.MinMagMipLinear
            addressU WrapMode.Wrap
            addressV WrapMode.Wrap
        }

    let private samplerBRDFLtu =
        sampler2d {
            texture uniform?BRDFLtu
            filter Filter.MinMagMipLinear
            addressU WrapMode.Clamp
            addressV WrapMode.Clamp
        }

    let ambientOcc =
        sampler2d {
            texture uniform?AmbientOcclusion
            addressU WrapMode.Clamp
            addressV WrapMode.Clamp
            filter Filter.MinMagLinear
        }

    [<ReflectedDefinition>]
    let getSpecularDominantDirArea n v roughness =
        // Simple linear approximation 
        let r = - Vec.reflect v n
        let  lerpFactor = (1.0 - roughness);
        Lerp n r lerpFactor |> Vec.normalize

    // o		: ray origin
    // d		: ray direction
    // returns distance on the ray to the object if hit, 0 otherwise
    [<ReflectedDefinition>]
    let tracePlane (o : V3d) (d : V3d) (planeOrigin : V3d) (planeNormal : V3d) =
        (Vec.dot planeNormal (planeOrigin - o)) / (Vec.dot planeNormal d)


    [<ReflectedDefinition>]
    let illuminannceSphereDisc cosTheta sinSigmaSqr =
        let sinTheta = sqrt (1.0 - cosTheta * cosTheta)
        let illuminance  =
            if cosTheta * cosTheta > sinSigmaSqr
            then  
                Math.PI * sinSigmaSqr * clamp 0.0 1.0 cosTheta
            else
                let x = sqrt(1.0 / sinSigmaSqr - 1.0) 
                let y = -x * (cosTheta / sinTheta)
                let sinThetaSqrtY = sinTheta * sqrt(1.0 - y * y)
                (cosTheta * acos(y) - x * sinThetaSqrtY) * sinSigmaSqr + atan(sinThetaSqrtY / x)
        illuminance
        |> max 0.0

    [<ReflectedDefinition>]
    let getLightParams (light : SLEUniform.Light) (wPos : V3d) (n : V3d) v roughness= 
        match  light.lightType  with
        | SLEUniform.LightType.DirectionalLight -> true, -light.lightDirection.XYZ |> Vec.normalize, light.color, 1.0
        | SLEUniform.LightType.PointLight -> 
            let lDir = light.lightPosition.XYZ - wPos |> Vec.normalize
            let dist = Vec.Distance (light.lightPosition.XYZ, wPos)
            let attenuation = 1.0 / (1.0 + light.attenuationLinear * dist + light.attenuationQad * dist * dist)
            true, lDir, light.color * attenuation, 1.0  
        | SLEUniform.LightType.SpotLight -> 
            let lDir = light.lightPosition.XYZ - wPos |> Vec.normalize
            let spotDir = -light.lightDirection.XYZ |> Vec.normalize
            let cosTheta = Vec.dot lDir spotDir
            let epsilon = light.cutOffInner - light.cutOffOuter
            let intensity = (cosTheta - light.cutOffOuter) / epsilon |> clamp 0.0 1.0
            let dist = Vec.Distance (light.lightPosition.XYZ, wPos)
            let attenuation = 1.0 / (1.0 + light.attenuationLinear * dist + light.attenuationQad * dist * dist)
            true, lDir, light.color * intensity * attenuation, 1.0             
        | SLEUniform.LightType.SphereLight ->
            let lUnnorm = light.lightPosition.XYZ - wPos
            let lDir = lUnnorm |> Vec.normalize
            let dist2  = Vec.dot lUnnorm lUnnorm
            let radius2 = light.radius * light.radius  
            let cosTheta = Vec.dot n lDir |> clamp -0.999 0.999
            let sinSigmaSqr = radius2/dist2 |> min 0.9999
            let attenuation = illuminannceSphereDisc cosTheta sinSigmaSqr

            let r = getSpecularDominantDirArea n v roughness
            let centerToRay = (Vec.dot lUnnorm r) * r - lUnnorm
            let l = 
                lUnnorm + centerToRay * clamp 0.0 1.0 (light.radius / length centerToRay) 
                |> Vec.normalize
            true, l , light.color * attenuation, 1.0
        | SLEUniform.LightType.DiskLight -> 
            let lUnnorm = light.lightPosition.XYZ - wPos
            let lDir = lUnnorm |> Vec.normalize
            let diskDir = light.lightDirection.XYZ |> Vec.normalize
            let cosTheta = Vec.dot n lDir |> clamp -0.999 0.999
            let dist2  = Vec.dot lUnnorm lUnnorm
            let radius2 = light.radius * light.radius  
            let sinSigmaSqr = radius2 / (radius2 + max radius2 dist2)
            let diskDirDotL = Vec.dot diskDir -lDir  |> saturate
            let attenuation = illuminannceSphereDisc cosTheta sinSigmaSqr * diskDirDotL

            let epsilon = light.cutOffInner - light.cutOffOuter
            let ct =  Vec.dot lDir -diskDir
            let intensity = (ct - light.cutOffOuter) / epsilon |> saturate

            let r = getSpecularDominantDirArea n v roughness
            let t = tracePlane wPos r light.lightPosition.XYZ diskDir
            let p = wPos + r * t
            let centerToRay = p - light.lightPosition.XYZ
            let l = 
                lUnnorm + centerToRay * saturate (light.radius / length centerToRay) 
                |> Vec.normalize

            let specularAttenuation = 
                Vec.dot diskDir r
                |> abs
                |> saturate 

            true, l, light.color * intensity * attenuation, specularAttenuation    
        | SLEUniform.LightType.NoLight -> false, V3d(0.0), V3d(0.0), 1.0
        |_ ->  false, V3d(0.0), V3d(0.0), 1.0  //allways match any cases, otherwise fshade will give a cryptic error 

    [<ReflectedDefinition>]
    let pbrDirect f0 roughness metallic (albedo : V3d) (wPos : V4d) v n nDotV light  = 
           
        let (exists, lDir, illuminannce, specularAttenuation)  = getLightParams light wPos.XYZ n v roughness
      
        let oi = 
            if exists then
                let h = v + lDir |> Vec.normalize

                // cook-torrance brdf
                let ndf = DistributionGGX n h roughness 
                let g = GeometrySmith false n v lDir roughness 
                let hDotV = Vec.dot h v |>  max 0.0      
                let kS = fresnelSchlick f0 hDotV   
                let nDotL = Vec.dot n lDir |>  max 0.0
            
                let kD = (1.0 - metallic) * (V3d.III - kS)
                let diffuse = kD * albedo / Math.PI

                let numerator = ndf * g * kS
                let denominator = 4.0 * nDotV * nDotL |> max 0.001
                let specular = numerator / denominator  

                // add to outgoing radiance from single light
                (diffuse + specular * specularAttenuation) * illuminannce * nDotL; 

            else V3d.Zero
        oi

    [<ReflectedDefinition>]
    let pBRLightning metallic roughness albedo n (wPos : V4d) =
        
        let cameraPos = uniform.CameraLocation

        let v = cameraPos - wPos.XYZ |> Vec.normalize
        let r = Vec.reflect -v n

        //asume 0.04 as F0 for non metals, set albedo as specular color for metallics
        let f0 = Lerp (V3d(0.04)) albedo metallic

        let nDotV = Vec.dot n v |>  max 0.0
        let light = uniform.Light
        let directLight = pbrDirect f0 roughness metallic albedo wPos v n nDotV light
        directLight

    let lightingDeferred (frag : Fragment)  =
        fragment {
            let col = 
                if frag.metallic < 0.0 then //no lighting, just put out the color      
                    V3d.Zero
                else //PBR lightning
                    let metallic = frag.metallic
                    let roughness = frag.roughness
                    let albedo = frag.c.XYZ
                    let n = frag.n
                    let wPos = frag.wp
                    pBRLightning metallic roughness albedo n wPos

            return V4d(col, 1.0)
        }

    [<ReflectedDefinition>]
    let pBRAbientLight f0 roughness metallic (albedo : V3d) n r nDotV =
        let kSA = fresnelSchlickRoughness f0 roughness nDotV
        let kdA  = (1.0 - kSA) * (1.0 - metallic)
        let irradiance = diffuseIrradianceSampler.Sample(n).XYZ
        let diffuse = irradiance * albedo

        let maxReflectLod = 4.0
        let prefilteredColor = prefilteredSpecColorSampler.SampleLevel(r, roughness * maxReflectLod).XYZ
        let brdf = samplerBRDFLtu.Sample(V2d(nDotV, roughness)).XY
        let specular = prefilteredColor * (kSA * brdf.X + brdf.Y)

        let ambient = kdA * diffuse + specular
        let ambientIntensity = uniform.AmbientIntensity
        ambient * ambientIntensity

    [<ReflectedDefinition>]
    let pBRAbient metallic roughness albedo n (wPos : V4d) =

        let cameraPos = uniform.CameraLocation
        let v = cameraPos - wPos.XYZ |> Vec.normalize
        let r = Vec.reflect -v n

        //asume 0.04 as F0 for non metals, set albedo as specular color for metallics
        let f0 = Lerp (V3d(0.04)) albedo metallic

        let nDotV = Vec.dot n v |>  max 0.0
        let ambient = pBRAbientLight f0 roughness metallic albedo n r nDotV
        ambient   


    let abientDeferred (frag : Fragment) =
        fragment {
            let occlusion = ambientOcc.Sample(frag.tc).X
            let col = 
                if frag.metallic < 0.0 then //no lighting, just put out the color      
                    frag.c.XYZ
                else //PBR lightning
                    let metallic = frag.metallic
                    let roughness = frag.roughness
                    let albedo = frag.c.XYZ
                    let n = frag.n
                    let wPos = frag.wp
                    let c = pBRAbient metallic roughness albedo n wPos
                    c * occlusion

            let em = frag.emission
            return V4d(em + col, 1.0)
        }

    let abientDeferredSimple (frag : Fragment) =
        fragment {
            let col = 
                if frag.metallic < 0.0 then //no lighting, just put out the color      
                    frag.c.XYZ
                else //ambient
                    let albedo = frag.c.XYZ
                    let ambientIntensity = uniform.AmbientIntensity
                    albedo * ambientIntensity
            let em = frag.emission
            return V4d(em + col, 1.0)
        }




