﻿using Ardalis.Result;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyKnowledgeManager.Core.Interfaces;
using MyKnowledgeManager.WebApi.ApiModels;
using MyKnowledgeManager.WebApi.Utilities;

namespace MyKnowledgeManager.WebApi.Controllers
{
    /// <summary>
    /// This controller is used as the "Knowledge" and its' children API.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class KnowledgeController : ControllerBase
    {
        private readonly IKnowledgeService _knowledgeService;
        private readonly IKnowledgeTagService _knowledgeTagService;
        private readonly IKnowledgeTagRelationService _knowledgeTagRelationService;
        private readonly IMapper _mapper;
        private readonly ITrashManager<Knowledge> _trashManager;
        private const string GeneralProblemMessage = "Something went wrong. Please try again.";
        private readonly string _userId;

        public KnowledgeController(
            IKnowledgeService knowledgeService,
            IKnowledgeTagService knowledgeTagService,
            IKnowledgeTagRelationService knowledgeTagRelationService,
            IMapper mapper,
            ITrashManager<Knowledge> trashManager)
        {
            _knowledgeService = knowledgeService;
            _knowledgeTagService = knowledgeTagService;
            _knowledgeTagRelationService = knowledgeTagRelationService;
            _mapper = mapper;
            _trashManager = trashManager;

            _userId = User.FindFirst("sub").Value;
        }

        // GET: api/Knowledge
        [HttpGet("{includeTags?}")]
        public async Task<ActionResult<List<KnowledgeDTO>>> GetKnowledgeList(bool includeTags = false)
        {
            List<Knowledge> knowledge = await _knowledgeService.GetKnowledgeListAsync(includeTags, _userId);
            
            return _mapper.Map<List<KnowledgeDTO>>(knowledge);
        }

        // GET: api/Knowledge/<GUID>
        [HttpGet("{id}/{includeTags?}")]
        public async Task<ActionResult<KnowledgeDTO>> GetKnowledge(string id, bool includeTags = false)
        {
            if (id is null) return BadRequest();

            Knowledge knowledge = await _knowledgeService.GetKnowledgeByIdAsync(id, includeTags);

            if (knowledge is null) return Problem();

            if (knowledge.UserId != _userId) return Unauthorized();

            return _mapper.Map<KnowledgeDTO>(knowledge);
        }

        // GET: api/Knowledge/getTrashKnowledge
        [HttpGet("getTrashKnowledge")]
        public async Task<ActionResult<List<KnowledgeDTO>>> GetTrashKnowledge()
        {
            var trashKnowledge = await _trashManager.GetTrashItemsAsync(_userId);

            if (trashKnowledge.Value is null || trashKnowledge.Value.Count() is 0) return NoContent();

            return _mapper.Map<List<KnowledgeDTO>>(trashKnowledge);
        }

        // POST: api/Knowledge
        [HttpPost("knoweldge")]
        [HttpPut("knowledge")]
        public async Task<ActionResult<KnowledgeDTO>> CreateKnowledge(KnowledgeDTO knowledgeDTO)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem();
            }

            // Converting KnowledgeDTO object to Knowledge object.
            Knowledge knowledge = _mapper.Map<Knowledge>(knowledgeDTO);

            if (knowledge is null)
            {
                return Problem(GeneralProblemMessage);
            }

            if (knowledge.UserId is null) knowledge.UpdateUserId(_userId);

            #region Create or Update Tags
            if (knowledgeDTO.KnowledgeTags is not null)
            {
                var updatedTagsResult = await UpdateDatabaseTagsAsync(knowledgeDTO.KnowledgeTags);

                if (!updatedTagsResult.Item1) return Problem(GeneralProblemMessage);
            }
            #endregion

            #region Create or Update Knowledge
            Result<Knowledge> result = null;

            if (Request.Method == HttpMethods.Put)
            {
                // Updating Knowledge
                result = await _knowledgeService.UpdateKnowledgeAsync(knowledge);
            }
            else if (Request.Method == HttpMethods.Post)
            {
                // Adding knowledge to the database
                result = await _knowledgeService.AddKnowledgeAsync(knowledge);
            }
            else
            {
                return BadRequest();
            }

            if (!result.IsSuccess) return Problem(GeneralProblemMessage);

            knowledge = result.Value;
            #endregion


            #region Create or Update Relations
            // Adding knowledge relations
            bool addRelationsResult = await AddKnowledgeTagRelationsAsync(
                updatedTagsResult.Item2
                .Select(x => x.Id)
                .ToList(),
                knowledge.Id);

            // Checking if operation was successful.
            if (!addRelationsResult)
            {
                await DeleteKnowledge(knowledge.Id);

                Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
            #endregion

            knowledgeDTO = _mapper.Map<KnowledgeDTO>(knowledge);

            return knowledgeDTO;
        }

        // PUT: api/Knowledge/moveKnowledgeToTrash/<GUID>
        [HttpPut("moveKnowledgeToTrash/{id}")]
        public async Task<ActionResult> MoveToTrashKnowledge(string id)
        {
            if (id is null) return BadRequest();

            // Moving input item to the trash
            var result = await _trashManager.MoveItemToTrashAsync(id, _userId);

            if (!result.IsSuccess)
            {
                return Problem(GeneralProblemMessage);
            }

            return Ok();
        }

        // PUT: api/Knowledge/restoreKnowledge/<GUID>
        [HttpPut("restoreKnowledge/{id}")]
        public async Task<IActionResult> RestoreKnowledge(string id)
        {
            if (id is null) return BadRequest();

            // Moving out input item from the trash
            var result = await _trashManager.RestoreTrashItemAsync(id, _userId);

            if (!result.IsSuccess)
            {
                return Problem(GeneralProblemMessage);
            }

            return Ok();
        }

        // DELETE: api/Knowledge/<GUID>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteKnowledge(string id)
        {
            if (id is null) return BadRequest();

            // Removing the knowledge from the database.
            var result = await _knowledgeService.RemoveKnowledgeAsync(id, _userId);

            if (!result.IsSuccess) return Problem();

            return Ok();
        }

        /// <summary>
        /// This function updates database tags by detecting previous and new tags.
        /// </summary>
        /// <param name="tags">An array of tags sent by the user.</param>
        /// <returns></returns>
        [NonAction]
        private async Task<(bool, List<KnowledgeTag>)> UpdateDatabaseTagsAsync(string[] tags)
        {
            if (tags is null || tags.Count() is 0) return (true, null);

            // Considering two lists, one list for old tags that exist on the database,
            // and one list of new tags we must add to the database.
            List<KnowledgeTag> dbTags = new();
            List<KnowledgeTag> newTags = new();

            #region Detecting previous and new tags
            // Iterate over all tags input items
            for (int i = 0; i < tags.Count(); i++)
            {
                var finalizedValue = KnowledgeTagHelper.FinalizeTagString(tags[i]);
                // TagName is unique, So, if we can get the tag from the database if exists.
                KnowledgeTag knowledgeTag = await _knowledgeTagService.GetKnowledgeTagByNameAsync(finalizedValue, _userId);

                // Checking if the knwoledgeTag is null, then we should add it to the newTags list.
                if (knowledgeTag is null)
                {
                    knowledgeTag = new(finalizedValue, _userId);

                    newTags.Add(knowledgeTag);
                }
                else
                {
                    dbTags.Add(knowledgeTag);
                }
            }
            #endregion

            #region Adding new tags to the database
            if (newTags.Count is not 0)
            {
                try
                {
                    // Adding all new tags to the database.
                    newTags = (List<KnowledgeTag>)await _knowledgeTagService.AddRangeKnowledgeTagAsync(newTags);
                }
                catch (Exception)
                {
                    return (false, null);
                }
            }
            #endregion

            // Since the operation was successful; we must combine tag lists.
            dbTags.AddRange(newTags);

            return (true, dbTags);
        }

        /// <summary>
        /// This function handles the relation between <see cref="Knowledge"/> and <see cref="KnowledgeTag"/>.
        /// </summary>
        /// <param name="tagIds">A list of probable tag ids for relation creation.</param>
        /// <param name="knowledgeId">The target <see cref="Knowledge"/> object for relation handling.</param>
        /// <returns></returns>
        [NonAction]
        private async Task<bool> AddKnowledgeTagRelationsAsync(List<string> tagIds, string knowledgeId)
        {
            #region Removing Old Relations
            // Getting old relations if exist.
            List<KnowledgeTagRelation> oldRelations = (List<KnowledgeTagRelation>)await _knowledgeTagRelationService.GetKnowledgeTagRelationsByKnowledgeIdAsync(knowledgeId);

            if (oldRelations is not null && oldRelations.Count() is not 0)
            {
                bool result = await _knowledgeTagRelationService.RemoveRangeTagsAsync(oldRelations);

                if (!result)
                {
                    return false;
                }

                oldRelations = null;
            }
            #endregion

            #region Creating New Relations
            // Checking if any tag Id exits for adding to the database.
            if (tagIds is not null && tagIds.Count is not 0)
            {
                // Initializing a list of relations for the database.
                List<KnowledgeTagRelation> knowledgeTagRelations = new();

                // Iterate over each Id in tagIds list to create a relationship object.
                for (int i = 0; i < tagIds.Count; i++)
                {
                    KnowledgeTagRelation knowledgeTagRelation = new(knowledgeId, tagIds[i]);
                    knowledgeTagRelations.Add(knowledgeTagRelation);
                }

                // Adding all KnowledgeTagRelation objects to the database.
                var result = await _knowledgeTagRelationService.AddRangeKnowledgeTagRelationAsync(knowledgeTagRelations);

                if (!result.IsSuccess) return false;
            }
            #endregion

            return true;
        }
    }
}
