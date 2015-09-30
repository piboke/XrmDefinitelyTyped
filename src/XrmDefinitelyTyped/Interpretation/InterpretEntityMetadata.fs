﻿namespace DG.XrmDefinitelyTyped

open IntermediateRepresentation
open InterpretOptionSetMetadata
open Microsoft.Xrm.Sdk.Metadata

module internal InterpretEntityMetadata =
  
  let toSome convertFunc (nullable:System.Nullable<'a>) =
    match nullable.HasValue with
    | true -> convertFunc (nullable.GetValueOrDefault())
    | false -> Type.Any

  let typeConv = function   
    | AttributeTypeCode.Boolean   -> Type.Boolean
    | AttributeTypeCode.DateTime  -> Type.Date
    | AttributeTypeCode.Integer   -> Type.Number
    
    | AttributeTypeCode.Memo      
    | AttributeTypeCode.EntityName
    | AttributeTypeCode.String    -> Type.String

    | AttributeTypeCode.BigInt    
    | AttributeTypeCode.Double    
    | AttributeTypeCode.Decimal   
    | AttributeTypeCode.Integer
    | AttributeTypeCode.Money     
    | AttributeTypeCode.Picklist  
    | AttributeTypeCode.State     
    | AttributeTypeCode.Status    -> Type.Number
    | _                           -> Type.Any

  let (|IsWrongYomi|) (haystack : string) =
    not(haystack.StartsWith("Yomi")) && haystack.Contains("Yomi")



  let interpretAttribute (a:AttributeMetadata) =
    let aType = a.AttributeType.GetValueOrDefault()
    match aType, a.SchemaName with
      | AttributeTypeCode.Virtual, _
      | _, IsWrongYomi true           -> None, None
      | _ -> 

      let options =
        match a with
        | :? EnumAttributeMetadata as eam -> interpretOptionSet eam.OptionSet
        | _ -> None

      let vType, sType = 
        match aType with
        | AttributeTypeCode.Money     -> Type.Number, SpecialType.Money
        | AttributeTypeCode.Picklist
        | AttributeTypeCode.State
        | AttributeTypeCode.Status    -> Type.Custom options.Value.displayName, SpecialType.OptionSet

        | AttributeTypeCode.Lookup    
        | AttributeTypeCode.PartyList
        | AttributeTypeCode.Customer
        | AttributeTypeCode.Owner     -> Type.Any, SpecialType.EntityReference
        
        | AttributeTypeCode.Uniqueidentifier -> Type.String, SpecialType.Guid
        | _ -> toSome typeConv a.AttributeType, SpecialType.Default
    
      options, Some {
        XrmAttribute.schemaName = a.SchemaName
        logicalName = a.LogicalName
        varType = vType
        specialType = sType }


  let interpretRelationship map referencing (rel:OneToManyRelationshipMetadata) =
    let rEntity =
        if referencing then rel.ReferencedEntity
        else rel.ReferencingEntity
        |> fun s ->
          match Map.tryFind s map with
          | Some x -> x
          | None -> s
    
    let name =
        match rel.ReferencedEntity = rel.ReferencingEntity with
        | false -> rel.SchemaName
        | true  ->
          match referencing with
          | true  -> sprintf "Referencing%s" rel.SchemaName
          | false -> sprintf "Referenced%s" rel.SchemaName

    let xRel = 
      { XrmRelationship.schemaName = name
        attributeName = 
          if referencing then rel.ReferencingAttribute 
          else rel.ReferencedAttribute
        referencing = referencing
        relatedEntity = rEntity }

    rEntity, xRel

//  let interpretRelationshipDEPRECATED map referencing (rel:OneToManyRelationshipMetadata) =
//    let rEntity =
//      if referencing then rel.ReferencedEntity
//      else rel.ReferencingEntity
//      |> fun s ->
//        match Map.tryFind s map with
//        | Some x -> x
//        | None -> s
//
//    let interpretRelationship' isResult =
//      let rType = 
//        if referencing then Type.Custom rEntity
//        else if isResult then
//          Type.SpecificGeneric ("SDK.Results", Type.Custom (sprintf "%sResult" rEntity)) 
//        else Type.Array (Type.Custom rEntity)
//
//      let name =
//        match rel.ReferencedEntity = rel.ReferencingEntity with
//        | false -> rel.SchemaName
//        | true  ->
//          match referencing with
//          | true  -> sprintf "Referencing%s" rel.SchemaName
//          | false -> sprintf "Referenced%s" rel.SchemaName
// 
//      { XrmAttribute.schemaName = name
//        logicalName = 
//          if referencing then rel.ReferencingAttribute 
//          else rel.ReferencedAttribute
//        varType = rType
//        specialType = SpecialType.Default }
//
//    rEntity, interpretRelationship'

  let interpretM2MRelationship map lname (rel:ManyToManyRelationshipMetadata) =
    let rEntity =
      match lname = rel.Entity2LogicalName with
      | true  -> rel.Entity1LogicalName
      | false -> rel.Entity2LogicalName
      |> fun s -> 
        match Map.tryFind s map with
        | Some x -> x
        | None -> s

    let xRel = 
      { XrmRelationship.schemaName = rel.SchemaName 
        attributeName = rel.SchemaName
        referencing = false
        relatedEntity = rEntity }
    
    rEntity, xRel

//  let interpretM2MRelationshipDEPRECATED map lname (rel:ManyToManyRelationshipMetadata) =
//    let rEntity =
//      match lname = rel.Entity2LogicalName with
//      | true  -> rel.Entity1LogicalName
//      | false -> rel.Entity2LogicalName
//      |> fun s -> 
//        match Map.tryFind s map with
//        | Some x -> x
//        | None -> s
//
//    let interpretM2MRelationship' isResult =
//      { XrmAttribute.schemaName = rel.SchemaName
//        logicalName = rel.SchemaName
//        varType = 
//          if isResult then Type.SpecificGeneric ("SDK.Results", Type.Custom (sprintf "%sResult" rEntity)) 
//          else Type.Array (Type.Custom rEntity) 
//        specialType = SpecialType.Default
//      }
//    
//    rEntity, interpretM2MRelationship'

  let interpretEntity map (metadata:EntityMetadata) =
    if (metadata.Attributes = null) then failwith "No attributes found!"

    let opt_sets, attr_vars = 
      metadata.Attributes 
      |> Array.map interpretAttribute
      |> Array.unzip

    let attr_vars = attr_vars |> Array.choose id |> Array.toList
    
    let opt_sets = 
      opt_sets |> Seq.choose id |> Seq.distinctBy (fun x -> x.displayName) 
      |> Seq.toList
    

    let handleOneToMany referencing = function
      | null -> Array.empty
      | x -> x |> Array.map (interpretRelationship map referencing)
    
    let handleManyToMany logicalName = function
      | null -> Array.empty
      | x -> x |> Array.map (interpretM2MRelationship map logicalName)


    let rel_entities, rel_vars = 
      [ metadata.OneToManyRelationships |> handleOneToMany false 
        metadata.ManyToOneRelationships |> handleOneToMany true 
        metadata.ManyToManyRelationships |> handleManyToMany metadata.LogicalName 
      ] |> List.map (Array.toList) 
        |> List.concat
        |> List.unzip

    let rel_entities = 
      rel_entities 
      |> Set.ofList |> Set.remove metadata.SchemaName |> Set.toList

    { XrmEntity.typecode = metadata.ObjectTypeCode.GetValueOrDefault()
      schemaName = metadata.SchemaName
      logicalName = metadata.LogicalName
      attr_vars = attr_vars
      rel_vars = rel_vars
      opt_sets = opt_sets
      relatedEntities = rel_entities }