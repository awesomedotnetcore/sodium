﻿module Sodium.Cell

let sink<'T> initialValue = new CellSink<'T>(new CellSinkImpl<'T>(initialValue))

let sinkWithCoalesce<'T> initialValue coalesce = new CellSink<'T>(new CellSinkImpl<'T>(initialValue, coalesce))

let send a (cellSink : 'T CellSink) = cellSink.Impl.Send a

let loop (f : 'T Cell -> ('T Cell * 'a)) =
    Transaction.Run (fun () ->
        let l = new CellLoop<'T>(new CellLoopImpl<'T>())
        let (c, r) = f l
        l.Impl.Loop(c.Impl)
        (c, r))

let loopWithNoCaptures (f : 'T Cell -> 'T Cell) =
    let (l, _) = loop (fun c -> (f c, ()))
    l

let constant<'T> value = new Cell<'T>(new CellImpl<'T>(value = value))

let constantLazy<'T> value = Stream.never<'T> () |> Stream.holdLazy value

let sample (cell : 'T Cell) = Transaction.Apply (fun _ -> cell.Impl.SampleNoTransaction ())

let sampleLazy (cell : 'T Cell) = Transaction.Apply cell.Impl.SampleLazy

let internal valueInternal (transaction : Transaction) (cell : 'T Cell) =
    let spark = new Stream<unit>(new StreamImpl<unit>())
    transaction.Prioritized spark.Impl.Node (fun transaction -> spark.Impl.Send(transaction, ()))
    let initial = spark |> Stream.snapshotAndTakeCell cell
    initial |> Stream.merge (fun _ r -> r) (new Stream<'T>(cell.Impl.Updates transaction))

let listenWeak handler (cell : 'T Cell) = Transaction.Apply (fun transaction -> cell |> valueInternal transaction |> Stream.listenWeak handler)

let listen handler (cell : 'T Cell) = Transaction.Apply (fun transaction -> cell |> valueInternal transaction |> Stream.listen handler)

let map f (cell : 'T Cell) = Transaction.Apply(fun transaction -> new Stream<'T>(cell.Impl.Updates transaction) |> Stream.map f |> Stream.holdLazyInternal transaction (cell.Impl.SampleLazy transaction |> Lazy.map f))

let mapConst value stream = map (fun _ -> value) stream

let apply f (cell : 'T Cell) =
    Transaction.Apply (fun transaction ->
        let out = Stream.sink ()
        let outTarget = out.Impl.Node
        let inTarget = Node<unit>(0L)
        let (_, nodeTarget) = inTarget.Link (fun _ _ -> ()) outTarget
        let mutable fo = Option.None
        let mutable ao = Option.None
        let h = (fun (transaction : Transaction) (f : 'T -> 'a) (a : 'T) -> transaction.Prioritized out.Impl.Node (fun transaction -> out.Impl.Send(transaction, f a)))
        let listener1 = (f |> valueInternal transaction).Impl.ListenWithTransaction inTarget (fun transaction f ->
            fo <- Option.Some f
            match ao with
                | None -> ()
                | Some a -> h transaction f a)
        let listener2 = (cell |> valueInternal transaction).Impl.ListenWithTransaction inTarget (fun transaction a ->
            ao <- Option.Some a
            match fo with
                | None -> ()
                | Some f -> h transaction f a)
        new Stream<_>((((out.Impl.LastFiringOnly transaction).UnsafeAddCleanup listener1).UnsafeAddCleanup listener2).UnsafeAddCleanup
            (Listener.fromAction (fun () -> inTarget.Unlink nodeTarget))) |>
                Stream.holdLazy (lazy (f.Impl.SampleNoTransaction () (cell.Impl.SampleNoTransaction ()))))

let lift2 f (cell1 : 'T Cell) (cell2 : 'T2 Cell) =
    apply (cell1 |> map f) cell2

let lift3 f (cell1 : 'T Cell) (cell2 : 'T2 Cell) (cell3 : 'T3 Cell) =
    apply (apply (cell1 |> map f) cell2) cell3

let lift4 f (cell1 : 'T Cell) (cell2 : 'T2 Cell) (cell3 : 'T3 Cell) (cell4 : 'T4 Cell) =
    apply (apply (apply (cell1 |> map f) cell2) cell3) cell4
        
let lift5 f (cell1 : 'T Cell) (cell2 : 'T2 Cell) (cell3 : 'T3 Cell) (cell4 : 'T4 Cell) (cell5 : 'T5 Cell) =
    apply (apply (apply (apply (cell1 |> map f) cell2) cell3) cell4) cell5
                
let lift6 f (cell1 : 'T Cell) (cell2 : 'T2 Cell) (cell3 : 'T3 Cell) (cell4 : 'T4 Cell) (cell5 : 'T5 Cell) (cell6 : 'T6 Cell) =
    apply (apply (apply (apply (apply (cell1 |> map f) cell2) cell3) cell4) cell5) cell6
                
let lift7 f (cell1 : 'T Cell) (cell2 : 'T2 Cell) (cell3 : 'T3 Cell) (cell4 : 'T4 Cell) (cell5 : 'T5 Cell) (cell6 : 'T6 Cell) (cell7 : 'T7 Cell) =
    apply (apply (apply (apply (apply (apply (cell1 |> map f) cell2) cell3) cell4) cell5) cell6) cell7
                
let lift8 f (cell1 : 'T Cell) (cell2 : 'T2 Cell) (cell3 : 'T3 Cell) (cell4 : 'T4 Cell) (cell5 : 'T5 Cell) (cell6 : 'T6 Cell) (cell7 : 'T7 Cell) (cell8 : 'T8 Cell) =
    apply (apply (apply (apply (apply (apply (apply (cell1 |> map f) cell2) cell3) cell4) cell5) cell6) cell7) cell8

let liftAll f (cells : seq<#Cell<'T>>) =
    Transaction.Apply (fun transaction ->
        let c = List.ofSeq cells
        let values = c |> Seq.map (fun c -> c.Impl.SampleNoTransaction ()) |> Array.ofSeq
        let out = new StreamImpl<'a>()
        let initialValue = lazy (f (List.ofSeq values))
        let listeners = cells |> Seq.mapi (fun i cell ->
            (cell.Impl.Updates transaction).ListenInternal out.Node transaction (fun transaction v ->
                values.[i] <- v
                out.Send(transaction, f (List.ofArray values))
                ) false)
        new Stream<_>(out.UnsafeAddCleanup (Listener.fromSeq listeners)) |> Stream.holdLazy initialValue)

let calm (cell : 'T Cell when 'T : equality) =
    let initialValue = cell |> sampleLazy
    let initialValueOption = Lazy.map Option.Some initialValue
    Transaction.Apply (fun transaction -> new Stream<_>(cell.Impl.Updates transaction) |> Stream.calmInternal initialValueOption |> Stream.holdLazy initialValue)

let switchC (cell : Cell<#Cell<'T>>) =
    Transaction.Apply (fun transaction ->
        let za = cell |> sampleLazy |> Lazy.map sample
        let out = new StreamImpl<'T>()
        let mutable currentListener = Option<IListener>.None
        let h = (fun (transaction : Transaction) (c : 'T Cell) ->
            match currentListener with
                | None -> ()
                | Some l -> l.Unlisten()
            currentListener <- Option.Some ((c |> valueInternal transaction).Impl.ListenInternal out.Node transaction (fun t a -> out.Send(t, a)) false))
        let listener = (cell |> valueInternal transaction).Impl.ListenInternal out.Node transaction h false
        new Stream<_>(out.UnsafeAddCleanup listener) |> Stream.holdLazy za)

let switchS (cell : Cell<#Stream<'T>>) =
    Transaction.Apply (fun transaction ->
        let out = new StreamImpl<'T>()
        let mutable currentListener = (cell.Impl.SampleNoTransaction ()).Impl.ListenInternal out.Node transaction (fun t a -> out.Send(t, a)) false
        let h = (fun (transaction : Transaction) (s : 'T Stream) ->
            transaction.Last (fun () ->
                currentListener.Unlisten()
                currentListener <- s.Impl.ListenInternal out.Node transaction (fun t a -> out.Send(t, a)) true))
        let listener = (cell.Impl.Updates transaction).ListenInternal out.Node transaction h false
        new Stream<_>(out.UnsafeAddCleanup listener))