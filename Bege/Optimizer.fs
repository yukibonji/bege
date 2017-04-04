﻿module Bege.Optimizer

open System

open Bege.Common
open Bege.AST

let inlineChains (m : Map<IPState, Instruction list * LastInstruction>) =

    let inc k m =
        match Map.tryFind k m with
        | None -> Map.add k 1 m
        | Some c -> Map.add k (c+1) m

    let countReferences m =
            m |> Map.fold (fun cs _ (_, li) -> 
                match li with
                | Exit -> cs
                | Branch (l, r) -> inc l (inc r cs)
                | Rand (a, b, c, d) -> inc a (inc b (inc c (inc d cs)))
                | ToState n -> inc n cs) Map.empty
     
    let counts = countReferences m

    let canBeInlined fs =
        counts.[fs] = 1
    
    let isJump fs =
        match m.[fs] with
        | ([], ToState t) -> true
        | _ -> false
    
    let target fs = 
        match m.[fs] with // must be guarded by `isJump`
        | ([], ToState t) -> t

    let result =
        m
        |> Map.map (fun fs ((il, li) as value) ->
            match li with
            | ToState n when canBeInlined n || isJump n ->
                // printfn "Inlining %A into %A" n fs
                let il', li' = m.[n] in
                (List.append il il', li')
            | Branch (l, r) when isJump l || isJump r ->
                let l = if isJump l then target l else l
                let r = if isJump r then target r else r
                (il, Branch (l, r))
            | Rand (a, b, c, d)
                when isJump a || isJump b || isJump c || isJump d ->
                let a = if isJump a then target a else a
                let b = if isJump b then target b else b
                let c = if isJump c then target c else c
                let d = if isJump d then target d else d
                (il, Rand (sort4 (a, b, c, d)))
            | _ ->  value)

    let newCounts = countReferences result

    result |> Map.filter (fun k _ -> newCounts.ContainsKey k || k = programEntryState (* can't remove entry state *))

let rec optimizeChain = function
    // constant folding
    | Push x :: Push y :: Add :: is -> optimizeChain (Push (x + y) :: is)
    | Push x :: Push y :: Divide :: is -> optimizeChain (Push (x / y) :: is)
    | Push x :: Push y :: Multiply :: is -> optimizeChain (Push (x * y) :: is)
    | Push x :: Push y :: Subtract :: is -> optimizeChain (Push (x - y) :: is)
    | Push x :: Push y :: Greater :: is -> optimizeChain (Push (Convert.ToInt32(x > y)) :: is)
    | Push x :: Not :: is -> optimizeChain (Push (Convert.ToInt32((x = 0))) :: is)
    // eliminate unneeded nots
    | Not :: Not :: is -> optimizeChain is
    // eliminate unneeded flips
    | Push x :: Push y :: Flip :: is -> optimizeChain (Push y :: Push x :: is)
    | Flip :: Flip :: is -> optimizeChain is
    | Dup :: Flip :: is -> optimizeChain is
    // eliminate dead pushes
    | Push x :: Pop :: is -> optimizeChain is
    | Dup :: Pop :: is -> optimizeChain is
    | (i :: is) -> i :: optimizeChain is
    | [] -> []

let optimizeLast fs noInstructions = function
    // identical branches
    | Branch (l, r) when l = r -> ToState r

    // Rand with all identical:
    | Rand (a, b, c, d) when a = b && b = c && c = d -> ToState d

    // Rand that loops back to same function must eventually branch to another,
    // so if there are no instructions in this function we could just jump instead.
    //
    // There are two orders to check here - since Rand is always ordered (via ord4),
    // either fs < all or fs > all:
    | Rand (a, b, c, d) when noInstructions && a = fs && b = c && c = d -> ToState d
    | Rand (a, b, c, d) when noInstructions && a = fs && b = fs && c = d -> ToState d
    | Rand (a, b, c, d) when noInstructions && a = fs && b = fs && c = fs -> ToState d

    | Rand (a, b, c, d) when noInstructions && a = b && b = c && d = fs -> ToState a
    | Rand (a, b, c, d) when noInstructions && a = b && c = fs && d = fs -> ToState a
    | Rand (a, b, c, d) when noInstructions && b = fs && c = fs && d = fs -> ToState a

    | c -> c

let optimizeChains = Map.map (fun fs (insns, last) -> (fix optimizeChain insns, optimizeLast fs (List.isEmpty insns) last))

let collapseIdenticalChains (m : Map<IPState, Instruction list * LastInstruction>) : Map<IPState, Instruction list * LastInstruction> =

    // invert the map, now all states with the same instruction list are in the same slot
    let inverted = invertMap m

    // pick one state to represent the others (the 'minimum')
    let newMappings : Map<IPState, IPState> =
        Map.fold (fun m _ fss ->
            let min = List.min fss in
            List.fold (fun m fs -> Map.add fs min m) m fss) Map.empty inverted

    // rewrite all the last-instructions to remap their states
    inverted
    |> Map.toSeq
    |> Seq.map (fun ((is, last), fss) -> 
        let newLast =
            match last with
            | Exit -> Exit
            | Branch (one, two) -> Branch (newMappings.[one], newMappings.[two])
            | Rand (a, b, c, d) -> Rand (sort4 (newMappings.[a], newMappings.[b], newMappings.[c], newMappings.[d]))
            | ToState n -> ToState (newMappings.[n])
        (List.min fss, (is, newLast)))
    |> Map.ofSeq

let optimize (chains : Map<IPState, Instruction list * LastInstruction>)
    : Map<IPState, Instruction list * LastInstruction> = 

    fix (optimizeChains << inlineChains << collapseIdenticalChains) chains