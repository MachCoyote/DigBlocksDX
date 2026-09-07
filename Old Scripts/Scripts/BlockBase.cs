using NUnit.Framework;
using System;
using Unity.Burst;
using UnityEngine;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Collections;

[BurstCompile]
public struct BlockBase
{
    /*[BurstCompile]
    public static ref FixedString64Bytes BlockName(UInt16 id)
    {
        //return ref Blocks(id).name;
        //temp solution
        switch (id)
        {
            case 0:
                return "Air";
            case 1:
                return ref new FixedString64Bytes("Grass");
            case 2:
                return ref new FixedString64Bytes("Dirt");
            default:
                return ref new FixedString64Bytes("Air");
        }
    }*/

    [BurstCompile]
    public static BlockType BlockTransparencyType(UInt16 id)
    {
        //return Blocks(id).type;
        //temp solution
        switch (id)
        {
            case 0:
                return BlockType.Air;
            case 1:
                return BlockType.Solid;
            case 2:
                return BlockType.Solid;
            default:
                return BlockType.Air;
        }
    }

    [BurstCompile]
    public static bool BlockMoveable(UInt16 id)
    {
        //return Blocks(id).moveable;
        //temp solution
        switch (id)
        {
            case 0:
                return true;
            case 1:
                return false;
            case 2:
                return false;
            default:
                return true;
        }
    }

    /*[BurstCompile]
    private static ref BlockInfo Blocks(UInt16 id)
    {
        BlockInfo Air = new BlockInfo { name = "Air", type = BlockType.Air, moveable = true };

        switch (id)
        {
            case 0:
                return ref Air;
            case 1:
                return new BlockInfo 
                { 
                    name = "Grass", 
                    type = BlockType.Solid, 
                    moveable = false 
                };
            case 2:
                return new BlockInfo 
                { 
                    name = "Dirt", 
                    type = BlockType.Solid, 
                    moveable = false 
                };
            default:
                return new BlockInfo 
                { 
                    name = "Air", 
                    type = BlockType.Air, 
                    moveable = true 
                };
        }
    }*/

    [BurstCompile]
    public struct BlockInfo
    {
        public FixedString64Bytes name;
        public BlockType type;
        public bool moveable;
    }

    public enum BlockType
    {
        Air,
        Solid,
        Transparent
    }
}
