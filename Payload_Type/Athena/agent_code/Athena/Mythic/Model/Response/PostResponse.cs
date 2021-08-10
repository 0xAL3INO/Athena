﻿using Athena.Mythic.Model;
using System.Collections.Generic;

namespace Athena.Mythic.Hooks
{
    //If you post a response to the same taskuuid it will keep appending data to the task (good for long running programs)
    public class PostResponse
    {
        public string action;
        public List<ResponseSuccessResult> responses;
    }
}