﻿module Constraint
open TypedAst
open FParsec
open Extensions

// This code is based off of Norman Ramsey's implementation as
// described in the book Programming Languages: Build, Prove, and Compare

exception TypeError of string

type ErrorMessage = Lazy<string>
type Constraint = Equal of TyExpr * TyExpr * ErrorMessage
                | And of Constraint * Constraint
                | Trivial

let (=~=) t1 (t2, err) = Equal (t1, t2, err)
let (&&&) c1 c2 = And (c1, c2)

let rec conjoinConstraints cc =
    match cc with
    | [] -> Trivial
    | [c] -> c
    | c::cs -> c &&& (conjoinConstraints cs)

let emptyEnv = Map.empty
let idSubst = Map.empty
let compose = Map.merge

let bind = Map.add

let rec varsubst theta name =
    match Map.tryFind name theta with
    | None -> TyVar name
    | Some (TyVar x) -> varsubst theta x
    | Some x -> x

let rec varsubstCap kappa a =
    match Map.tryFind a kappa with
    | None -> CapacityVar a
    | Some (CapacityVar x) -> varsubstCap kappa x
    | Some x -> x

(*let varsubst theta a =
    match follow a theta with
    | None -> TyVar a
    | Some x -> x*)

(*let varsubstCap kappa a =
    match Map.tryFind a kappa with
    | None -> CapacityVar a
    | Some x -> x*)

let rec capsubst kappa =
    function
    | (CapacityVar a) -> varsubstCap kappa a
    | (CapacityOp {op=op; left=left; right=right}) -> CapacityOp {op=op; left=capsubst kappa left; right=capsubst kappa right}
    | (CapacityConst x) -> CapacityConst x
    | (CapacityUnaryOp {op=op; term=term}) -> CapacityUnaryOp {op=op; term=capsubst kappa term}

let tycapsubst theta kappa =
    let rec subst =
        function
        | (TyVar a) -> varsubst theta a
        | (TyCon c) -> TyCon c
        | (ConApp (tau, taus, caps)) -> ConApp (subst tau, List.map subst taus, List.map (capsubst kappa) caps)
    subst

let consubst theta kappa =
    let rec subst =
        function
        | (Equal (tau1, tau2, err)) ->
            Equal (tycapsubst theta kappa tau1, tycapsubst theta kappa tau2, err)
        | (And (c1, c2)) -> And (subst c1, subst c2)
        | Trivial -> Trivial
    subst

let eqType = (=)
let eqCap = (=)

let n = ref 1
let freshtyvar _ =
    let ret = TyVar (sprintf "t%i" !n)
    n := !n + 1
    ret

let n2 = ref 1
let freshcapvar _ =
    let ret = CapacityVar (sprintf "c%i" !n2)
    n := !n + 1
    ret

let rec freeCapVars =
    function
    | CapacityVar name -> Set.singleton name
    | CapacityOp {left=left; right=right} -> Set.union (freeCapVars left) (freeCapVars right)
    | CapacityConst _ -> Set.empty
    | CapacityUnaryOp {term=term} -> freeCapVars term

let rec freeVars t =
    let rec freeTyVars =
        function 
        | TyVar v -> (Set.singleton v, Set.empty)
        | TyCon _ -> (Set.empty, Set.empty)
        | ConApp (ty, tys, caps) ->
            let (ts, c1) = List.map freeVars (ty::tys) |> List.unzip
            let c2 = List.map freeCapVars caps
            (Set.unionMany ts, Set.union (Set.unionMany c1) (Set.unionMany c2))
    freeTyVars t

let freeTyVars t =
    freeVars t |> fst

let (|--->) a tau =
    match tau with
    | TyVar a' -> if a = a' then idSubst else bind a tau emptyEnv
    | _ ->
        if Set.contains a (freeTyVars tau) then
            failwith "non-idemptotent substitution"
        else
            bind a tau emptyEnv

let (|-%->) a cap =
    match cap with
    | CapacityVar a' -> if a = a' then idSubst else bind a cap emptyEnv
    | _ ->
        if Set.contains a (freeCapVars cap) then
            failwith "non-idemptotent capacity substitution"
        else
            bind a cap emptyEnv
    

let instantiate (Forall (formals, caps, tau)) actuals capActuals =
    tycapsubst (Map.ofList (List.zip formals actuals)) (Map.ofList (List.zip caps capActuals)) tau

let freshInstance (Forall (bound, caps, tau)) =
    instantiate (Forall (bound, caps, tau)) (List.map freshtyvar bound) (List.map freshcapvar caps)

let instantiateRecord (bound, caps, fields) actuals capActuals =
    let substitutions = List.zip bound actuals |> Map.ofList
    let capSubstitutions = List.zip bound capActuals |> Map.ofList
    fields |> Map.map (fun (fieldName : string) tau -> tycapsubst substitutions capSubstitutions tau)

let freshInstanceRecord (bound, caps, fields) =
    instantiateRecord (bound, caps, fields) (List.map freshtyvar bound) (List.map freshcapvar caps)

(*
let canonicalize (Forall (bound, ty)) =
    let canonicalTyvarName n =
        let letters = "abcdefghijklmnopqrstuvwxyz"
        if n < 26 then
            sprintf "%c" letters.[n]
        else
            sprintf "v%i" (n - 25)
    let free = Set.difference (freeTyVars ty) (Set.ofSeq bound)
    let rec unusedIndex n =
        if Set.contains (canonicalTyvarName n) free then
           unusedIndex (n+1)
        else
            n
    let rec newBoundVars =
        function
        | (_, []) -> []
        | (index, oldvar::oldvars) ->
            let n = unusedIndex index
            (canonicalTyvarName n) :: (newBoundVars (n+1, oldvars))
    let newBound = newBoundVars (0, bound)
    Forall (newBound, tysubst (Map.ofList (List.zip bound (List.map TyVar newBound))) ty)
*)

let generalize tyvars capvars tau =
    let (t, c) = freeVars tau
    Forall (Set.difference t tyvars |> List.ofSeq, Set.difference c capvars |> List.ofSeq, tau)

let solveTyvarEq a tau =
    if eqType (TyVar a) tau then
        Some idSubst
    elif Set.contains a (freeTyVars tau) then
        None // error
    else
        a |---> tau |> Some

let solveCapvarEq a cap =
    if eqCap (CapacityVar a) cap then
        Some idSubst
    elif Set.contains a (freeCapVars cap) then
        None
    else
        a |-%-> cap |> Some

let rec solve con : Map<string, TyExpr> * Map<string, CapacityExpr> =
    match con with
    | Trivial -> (idSubst, idSubst)
    | And (left, right) ->
        let (theta1, kappa1) = solve left
        let (theta2, kappa2) = solve (consubst theta1 kappa1 right)
        (compose theta1 theta2, compose kappa1 kappa2)
    | Equal (tau, tau', err) ->
        let failMsg = lazy (sprintf "Type error: The types %s and %s are not equal.\n\n%s" (typeString tau) (typeString tau') (err.Force()))
        match (tau, tau') with
        | ((TyVar a, tau) | (tau, TyVar a)) ->
            match solveTyvarEq a tau with
            | Some answer -> (answer, idSubst)
            | None -> raise <| TypeError (failMsg.Force())
        | (TyCon mu, TyCon mu') ->
            if mu = mu' then
                (idSubst, idSubst)
            else
                raise <| TypeError (failMsg.Force())
        | (ConApp (t, ts, cs), ConApp(t', ts', cs')) ->
            if List.length ts = List.length ts' && List.length cs = List.length cs' then
                let eqAnd c t t' = And ((Equal (t, t', err)), c)
                solve (List.fold2 eqAnd Trivial (t::ts) (t'::ts'))
            else
                raise <| TypeError (failMsg.Force())
        | _ -> raise <| TypeError (failMsg.Force())
