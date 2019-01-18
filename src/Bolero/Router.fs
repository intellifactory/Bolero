// $begin{copyright}
//
// This file is part of Bolero
//
// Copyright (c) 2018 IntelliFactory and contributors
//
// Licensed under the Apache License, Version 2.0 (the "License"); you
// may not use this file except in compliance with the License.  You may
// obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
// implied.  See the License for the specific language governing
// permissions and limitations under the License.
//
// $end{copyright}

namespace Bolero

#nowarn "40" // recursive value `segment` in getSegment

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Text
open FSharp.Reflection
open System.Runtime.InteropServices

/// A router that binds page navigation with Elmish.
type IRouter<'model, 'msg> =
    /// Get the uri corresponding to `model`.
    abstract GetRoute : model: 'model -> string

    /// Get the message to send when the page navigates to `uri`.
    abstract SetRoute : uri: string -> option<'msg>

/// A simple hand-written router.
type Router<'model, 'msg> =
    {
        /// Get the uri corresponding to `model`.
        getRoute: 'model -> string
        /// Get the message to send when the page navigates to `uri`.
        setRoute: string -> option<'msg>
    }

    interface IRouter<'model, 'msg> with
        member this.GetRoute(model) = this.getRoute model
        member this.SetRoute(uri) = this.setRoute uri

/// A simple router where the endpoint corresponds to a value easily gettable from the model.
type Router<'ep, 'model, 'msg> =
    {
        getEndPoint: 'model -> 'ep
        getRoute: 'ep -> string
        setRoute: string -> option<'msg>
    }

    /// Get the uri for the given endpoint.
    member this.Link(ep) = this.getRoute ep

    interface IRouter<'model, 'msg> with
        member this.GetRoute(model) = this.getRoute (this.getEndPoint model)
        member this.SetRoute(uri) = this.setRoute uri

/// Declare how an F# union case matches to a URI.
[<AttributeUsage(AttributeTargets.Property, AllowMultiple = false)>]
type EndPointAttribute(endpoint: string) =
    inherit Attribute()

    let endpoint = endpoint.Trim('/').Split('/')

    /// The path that this endpoint recognizes.
    member this.Path = endpoint

/// Declare that the given field of an F# union case matches the entire
/// remainder of the URL path.
/// If field is unspecified, this applies to the last field of the case.
[<AttributeUsage(AttributeTargets.Property, AllowMultiple = false)>]
type WildcardAttribute([<Optional>] field: string) =
    inherit Attribute()

    member this.Field = field

[<AutoOpen>]
module private RouterImpl =
    open System.Text.RegularExpressions

    type ArraySegment<'T> with
        member this.Item with get(i) = this.Array.[this.Offset + i]

    type SegmentParserResult = option<obj * list<string>>
    type SegmentParser = list<string> -> SegmentParserResult
    type SegmentWriter = obj -> list<string>
    type Segment =
        {
            parse: SegmentParser
            write: SegmentWriter
        }

    let fail : SegmentParserResult = None
    let ok x : SegmentParserResult = Some x

    let inline tryParseBaseType<'T when 'T : (static member TryParse : string * byref<'T> -> bool)> () =
        fun s ->
            let mutable out = Unchecked.defaultof<'T>
            if (^T : (static member TryParse : string * byref<'T> -> bool) (s, &out)) then
                Some (box out)
            else
                None

    let inline defaultBaseTypeParser<'T when 'T : (static member TryParse : string * byref<'T> -> bool)> = function
        | [] -> fail
        | x :: rest ->
            match tryParseBaseType<'T>() x with
            | Some x -> ok (box x, rest)
            | None -> fail

    let inline baseTypeSegment<'T when 'T : (static member TryParse : string * byref<'T> -> bool)> () =
        {
            parse = defaultBaseTypeParser<'T>
            write = fun x -> [string x]
        }

    let baseTypes : IDictionary<Type, Segment> = dict [
        typeof<string>, {
            parse = function
                | [] -> fail
                | x :: rest -> ok (box x, rest)
            write = unbox<string> >> List.singleton
        }
        typeof<bool>, {
            parse = defaultBaseTypeParser<bool>
            // `string true` returns capitalized "True", but we want lowercase "true".
            write = fun x -> [(if unbox x then "true" else "false")]
        }
        typeof<Byte>, baseTypeSegment<Byte>()
        typeof<SByte>, baseTypeSegment<SByte>()
        typeof<Int16>, baseTypeSegment<Int16>()
        typeof<UInt16>, baseTypeSegment<UInt16>()
        typeof<Int32>, baseTypeSegment<Int32>()
        typeof<UInt32>, baseTypeSegment<UInt32>()
        typeof<Int64>, baseTypeSegment<Int64>()
        typeof<UInt64>, baseTypeSegment<UInt64>()
        typeof<single>, baseTypeSegment<single>()
        typeof<float>, baseTypeSegment<float>()
        typeof<decimal>, baseTypeSegment<decimal>()
    ]

    let sequenceSegment getSegment (ty: Type) revAndConvert toListAndLength : Segment =
        let itemSegment = getSegment ty
        let rec parse acc remainingLength fragments =
            if remainingLength = 0 then
                ok (revAndConvert acc, fragments)
            else
                match itemSegment.parse fragments with
                | None -> None
                | Some (x, rest) ->
                    parse (x :: acc) (remainingLength - 1) rest
        {
            parse = function
                | x :: rest ->
                    match Int32.TryParse(x) with
                    | true, length -> parse [] length rest
                    | false, _ -> fail
                | _ -> fail
            write = fun x ->
                let list, (length: int) = toListAndLength x
                string length :: List.collect itemSegment.write list
        }

    let [<Literal>] FLAGS_STATIC =
        Reflection.BindingFlags.Static |||
        Reflection.BindingFlags.Public |||
        Reflection.BindingFlags.NonPublic

    let arrayRevAndUnbox<'T> (l: list<obj>) : 'T[] =
        let a = [|for x in l -> unbox<'T> x|]
        Array.Reverse(a)
        a

    let arrayLengthAndBox<'T> (a: array<'T>) : list<obj> * int =
        [for x in a -> box x], a.Length

    let arraySegment getSegment ty : Segment =
        let arrayRevAndUnbox =
            typeof<Segment>.DeclaringType.GetMethod("arrayRevAndUnbox", FLAGS_STATIC)
                .MakeGenericMethod([|ty|])
        let arrayLengthAndBox =
            typeof<Segment>.DeclaringType.GetMethod("arrayLengthAndBox", FLAGS_STATIC)
                .MakeGenericMethod([|ty|])
        sequenceSegment getSegment ty
            (fun l -> arrayRevAndUnbox.Invoke(null, [|l|]))
            (fun l -> arrayLengthAndBox.Invoke(null, [|l|]) :?> _)

    let listRevAndUnbox<'T> (l: list<obj>) : list<'T> =
        List.map unbox<'T> l |> List.rev

    let listLengthAndBox<'T> (l: list<'T>) : list<obj> * int =
        List.mapFold (fun l e -> box e, l + 1) 0 l

    let listSegment getSegment ty : Segment =
        let listRevAndUnbox =
            typeof<Segment>.DeclaringType.GetMethod("listRevAndUnbox", FLAGS_STATIC)
                .MakeGenericMethod([|ty|])
        let listLengthAndBox =
            typeof<Segment>.DeclaringType.GetMethod("listLengthAndBox", FLAGS_STATIC)
                .MakeGenericMethod([|ty|])
        sequenceSegment getSegment ty
            (fun l -> listRevAndUnbox.Invoke(null, [|l|]))
            (fun l -> listLengthAndBox.Invoke(null, [|l|]) :?> _)

    [<CustomEquality; NoComparison>]
    type ParameterModifier =
        | Basic
        //| Optional
        | Rest of (seq<obj> -> obj) * (obj -> seq<obj>)

        interface IEquatable<ParameterModifier> with
            member this.Equals(that) =
                match this, that with
                | Basic, Basic
                | Rest _, Rest _ -> true
                | _ -> false

    type UnionParserSegment =
        | Constant of string
        /// A UnionParserSegment can be common among multiple union cases.
        /// fieldIndex lists these cases, and for each of them, its total number of fields and the index of the field for this segment.
        | Parameter of fieldIndex: list<UnionCaseInfo * int * int> * fieldType: Type * fieldSegment: Segment * ParameterModifier

    type UnionParser =
        {
            /// Parser for the first segment.
            head: UnionParserSegment
            /// Parsers for the remaining segments, each item in the list for a different union case.
            tails: list<UnionParser>
            /// If Some, there is a case for which this is the final segment, and this is its constructor.
            finalize: option<UnionCaseInfo * (obj[] -> obj)>
        }

    let parseEndPointCasePath (case: UnionCaseInfo) =
        case.GetCustomAttributes()
        |> Array.tryPick (function
            | :? EndPointAttribute as e -> Some (List.ofSeq e.Path)
            | _ -> None)
        |> Option.defaultWith (fun () -> [case.Name])

    let isConstantFragment (s: string) =
        not (s.Contains("{"))

    type Unboxer =
        static member List<'T> (items: seq<obj>) : list<'T> =
            [ for x in items -> unbox<'T> x ]

        static member Array<'T> (items: seq<obj>) : 'T[] =
            [| for x in items -> unbox<'T> x |]

    type Decons =
        static member List<'T> (l: list<'T>) : seq<obj> =
            Seq.cast l

        static member Array<'T> (l: 'T[]) : seq<obj> =
            Seq.cast l

    let restModifierFor (ty: Type) =
        if ty = typeof<string> then
            ty, Rest(
                Seq.cast<string> >> String.concat "/" >> box,
                fun s -> (unbox<string> s).Split('/') |> Seq.cast<obj>
            )
        elif ty.IsArray && ty.GetArrayRank() = 1 then
            let elt = ty.GetElementType()
            let unboxer = typeof<Unboxer>.GetMethod("Array", FLAGS_STATIC).MakeGenericMethod([|elt|])
            let decons = typeof<Decons>.GetMethod("Array", FLAGS_STATIC).MakeGenericMethod([|elt|])
            elt, Rest(
                (fun x -> unboxer.Invoke(null, [|x|])),
                (fun x -> decons.Invoke(null, [|x|]) :?> _)
            )
        elif ty.IsGenericType then
            let tdef = ty.GetGenericTypeDefinition()
            if tdef = typedefof<list<_>> then
                let targs = ty.GetGenericArguments()
                let unboxer = typeof<Unboxer>.GetMethod("List", FLAGS_STATIC).MakeGenericMethod(targs)
                let decons = typeof<Decons>.GetMethod("List", FLAGS_STATIC).MakeGenericMethod(targs)
                targs.[0], Rest(
                    (fun x -> unboxer.Invoke(null, [|x|])),
                    (fun x -> decons.Invoke(null, [|x|]) :?> _)
                )
            else
                failwithf "Invalid type for *rest parameter: %A" ty
        else
            failwithf "Invalid type for *rest parameter: %A" ty

    let fragmentParameterRE = Regex(@"^\{([?*]?)([a-zA-Z0-9_]+)\}$", RegexOptions.Compiled)

    let parseEndPointCase getSegment (case: UnionCaseInfo) =
        let fields = case.GetFields()
        let defaultFrags() =
            fields
            |> Array.mapi (fun i p ->
                let ty = p.PropertyType
                Parameter([case, fields.Length, i], ty, getSegment ty, Basic))
            |> List.ofSeq
        match parseEndPointCasePath case with
        // EndPoint "/"
        | [] -> defaultFrags()
        // EndPoint "/const"
        | [root] when isConstantFragment root -> Constant root :: defaultFrags()
        // EndPoint <complex_path>
        | frags ->
            let unboundFields = HashSet(fields |> Array.map (fun f -> f.Name))
            let res =
                frags
                |> List.map (fun frag ->
                    if isConstantFragment frag then
                        Constant frag
                    else
                        let m = fragmentParameterRE.Match(frag)
                        if m.Success then
                            let fieldName = m.Groups.[2].Value
                            match fields |> Array.tryFindIndex (fun p -> p.Name = fieldName) with
                            | Some i ->
                                let p = fields.[i]
                                if unboundFields.Remove(fieldName) then
                                    let ty = p.PropertyType
                                    let eltTy, modifier =
                                        match m.Groups.[1].Value with
                                        | "" -> ty, Basic
                                        | "*" -> restModifierFor ty
                                        | s -> failwithf "Invalid parameter modifier: %s" s
                                    Parameter([case, fields.Length, i], ty, getSegment eltTy, modifier)
                                else
                                    failwithf "Union case %s.%s has endpoint definition with duplicate field %s"
                                        case.DeclaringType.FullName case.Name fieldName
                            | None -> failwithf "Union case %s.%s has endpoint definition with undefined field %s"
                                        case.DeclaringType.FullName case.Name fieldName
                        else failwithf "Union case %s.%s has endpoint definition with invalid path fragment '%s'"
                                case.DeclaringType.FullName case.Name frag
                )
            if unboundFields.Count > 0 then
                failwithf "Union case %s.%s has endpoint definition with some but not all of its fields"
                    case.DeclaringType.FullName case.Name
            res

    let caseCtor (case: UnionCaseInfo) : UnionCaseInfo * (obj[] -> obj) =
        case, FSharpValue.PreComputeUnionConstructor(case, true)

    let rec mergeEndPointCaseFragments (cases: seq<UnionCaseInfo * list<UnionParserSegment>>) : list<UnionParser> * option<UnionCaseInfo * (obj[] -> obj)> =
        let constants = Dictionary<string, _>()
        let mutable parameter = None
        let mutable final = None
        cases |> Seq.iter (fun (case, p) ->
            match p with
            | Constant s :: rest ->
                let existing =
                    match constants.TryGetValue(s) with
                    | true, x -> x
                    | false, _ -> []
                constants.[s] <- (case, rest) :: existing
            | Parameter(n, ty, seg, modif) :: rest ->
                match parameter with
                | Some (n', ty', seg, ps, modif') ->
                    if ty <> ty' then
                        failwithf "[1] Union %s has cases with conflicting endpoint definitions" case.DeclaringType.FullName
                    elif modif <> modif' then
                        failwithf "[2] Union %s has cases with conflicting endpoint definitions" case.DeclaringType.FullName
                    else
                        parameter <- Some (n @ n', ty, seg, (case, rest) :: ps, modif)
                | None ->
                    parameter <- Some (n, ty, seg, [case, rest], modif)
            | [] ->
                match final with
                | Some _ ->
                    failwithf "[3] Union %s has cases with conflicting endpoint definitions" case.DeclaringType.FullName
                | None ->
                    final <- Some (caseCtor case)
        )
        [
            for KeyValue(s, cases) in constants do
                let tails, final = mergeEndPointCaseFragments cases
                yield {
                    head = Constant s
                    tails = tails
                    finalize = final
                }
            match parameter with
            | None -> ()
            | Some (n, ty, seg, cases, modif) ->
                let tails, final = mergeEndPointCaseFragments cases
                yield {
                    head = Parameter(n, ty, seg, modif)
                    tails = tails
                    finalize = final
                }
        ], final

    let parseUnion cases : SegmentParser =
        let parsers, final = mergeEndPointCaseFragments cases
        fun l ->
            let d = Dictionary<UnionCaseInfo, obj[]>()
            let rec run parsers final l =
                parsers
                |> Seq.tryPick (fun p ->
                    match p.head, l with
                    | Constant s, s' :: rest when s = s' ->
                        run p.tails p.finalize rest
                    | Constant _, _ ->
                        None
                    | Parameter(n, _, seg, Basic), l ->
                        match seg.parse l with
                        | None -> None
                        | Some (o, rest) ->
                            for (case, fieldCount, i) in n do
                                let a =
                                    match d.TryGetValue(case) with
                                    | true, a -> a
                                    | false, _ ->
                                        let a = Array.zeroCreate fieldCount
                                        d.[case] <- a
                                        a
                                a.[i] <- o
                            run p.tails p.finalize rest
                    | Parameter(n, _, seg, Rest(restBuild, _)), l ->
                        let restValues = ResizeArray()
                        let rec parse l =
                            match seg.parse l, l with
                            | None, [] ->
                                for (case, fieldCount, i) in n do
                                    let a =
                                        match d.TryGetValue(case) with
                                        | true, a -> a
                                        | false, _ ->
                                            let a = Array.zeroCreate fieldCount
                                            d.[case] <- a
                                            a
                                    a.[i] <- restBuild restValues
                                run p.tails p.finalize []
                            | None, _::_ -> None
                            | Some (o, rest), _ ->
                                restValues.Add(o)
                                parse rest
                        parse l
                )
                |> Option.orElseWith (fun () ->
                    final |> Option.map (fun (case, ctor) ->
                        let args =
                            match d.TryGetValue(case) with
                            | true, args -> args
                            | false, _ -> [||]
                        ctor args, l
                    )
                )
            run parsers final l

    let parseConsecutiveTypes getSegment (tys: Type[]) (ctor: obj[] -> obj) : SegmentParser =
        let fields = Array.map getSegment tys
        fun (fragments: list<string>) ->
            let args = Array.zeroCreate fields.Length
            let rec go i fragments =
                if i = fields.Length then
                    ok (ctor args, fragments)
                else
                    match fields.[i].parse fragments with
                    | None -> fail
                    | Some (x, rest) ->
                        args.[i] <- x
                        go (i + 1) rest
            go 0 fragments

    let writeConsecutiveTypes getSegment (tys: Type[]) (dector: obj -> obj[]) : SegmentWriter =
        let fields = tys |> Array.map (fun t -> (getSegment t).write)
        fun (r: obj) ->
            Array.map2 (<|) fields (dector r)
            |> List.concat

    let caseDector (case: UnionCaseInfo) : obj -> obj[] =
        FSharpValue.PreComputeUnionReader(case, true)

    let writeUnionCase (case: UnionCaseInfo, path: list<UnionParserSegment>) =
        let dector = caseDector case
        fun o ->
            let vals = dector o
            path |> List.collect (function
                | Constant s -> [s]
                | Parameter(n, _, seg, Basic) ->
                    let (_, _, i) = n |> List.find (fun (case', _, _) -> case' = case)
                    seg.write vals.[i]
                | Parameter(n, _, seg, Rest(_, decons)) ->
                    let (_, _, i) = n |> List.find (fun (case', _, _) -> case' = case)
                    [ for x in decons vals.[i] do yield! seg.write x ]
            )

    let unionSegment (getSegment: Type -> Segment) (ty: Type) : Segment =
        let cases =
            FSharpType.GetUnionCases(ty, true)
            |> Array.map (fun c -> c, parseEndPointCase getSegment c)
        let write =
            let writers = Array.map writeUnionCase cases
            let tagReader = FSharpValue.PreComputeUnionTagReader(ty, true)
            fun r -> writers.[tagReader r] r
        let parse = parseUnion cases
        { parse = parse; write = write }

    let tupleSegment getSegment ty =
        let tys = FSharpType.GetTupleElements ty
        let ctor = FSharpValue.PreComputeTupleConstructor ty
        let dector = FSharpValue.PreComputeTupleReader ty
        {
            parse = parseConsecutiveTypes getSegment tys ctor
            write = writeConsecutiveTypes getSegment tys dector
        }

    let recordSegment getSegment ty =
        let tys = FSharpType.GetRecordFields(ty, true) |> Array.map (fun p -> p.PropertyType)
        let ctor = FSharpValue.PreComputeRecordConstructor(ty, true)
        let dector = FSharpValue.PreComputeRecordReader(ty, true)
        {
            parse = parseConsecutiveTypes getSegment tys ctor
            write = writeConsecutiveTypes getSegment tys dector
        }

    let rec getSegment (cache: Dictionary<Type, Segment>) (ty: Type) : Segment =
        match cache.TryGetValue(ty) with
        | true, x -> unbox x
        | false, _ ->
            // Add lazy version in case ty is recursive.
            let rec segment = ref {
                parse = fun x -> (!segment).parse x
                write = fun x -> (!segment).write x
            }
            cache.[ty] <- !segment
            let getSegment = getSegment cache
            segment :=
                if ty.IsArray && ty.GetArrayRank() = 1 then
                    arraySegment getSegment (ty.GetElementType())
                elif ty.IsGenericType && ty.GetGenericTypeDefinition() = typedefof<list<_>> then
                    listSegment getSegment (ty.GetGenericArguments().[0])
                elif FSharpType.IsUnion(ty, true) then
                    unionSegment getSegment ty
                elif FSharpType.IsTuple(ty) then
                    tupleSegment getSegment ty
                elif FSharpType.IsRecord(ty, true) then
                    recordSegment getSegment ty
                else
                    failwithf "Router.Infer used with type %s, which is not supported." ty.FullName
            cache.[ty] <- !segment
            !segment

/// Functions for building Routers that bind page navigation with Elmish.
module Router =

    /// Infer a router constructed around an endpoint type `'ep`.
    /// This type must be an F# union type, and its cases should use `EndPointAttribute`
    /// to declare how they match to a URI.
    let infer<'ep, 'model, 'msg> (makeMessage: 'ep -> 'msg) (getEndPoint: 'model -> 'ep) =
        let ty = typeof<'ep>
        let cache = Dictionary()
        for KeyValue(k, v) in baseTypes do cache.Add(k, v)
        let frag = getSegment cache ty
        {
            getEndPoint = getEndPoint
            getRoute = fun ep ->
                box ep
                |> frag.write
                |> String.concat "/"
            setRoute = fun path ->
                path.Split('/')
                |> List.ofArray
                |> frag.parse
                |> Option.bind (function
                    | x, [] -> Some (unbox<'ep> x |> makeMessage)
                    | _ -> None)
        }

[<Extension>]
type RouterExtensions =

    [<Extension>]
    static member HRef(this: Router<'ep, _, _>, endpoint: 'ep) : Attr =
        Attr("href", this.Link endpoint)
