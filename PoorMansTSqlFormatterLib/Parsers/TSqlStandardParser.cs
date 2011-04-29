﻿/*
Poor Man's T-SQL Formatter - a small free Transact-SQL formatting 
library for .Net 2.0, written in C#. 
Copyright (C) 2011 Tao Klerks

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU Affero General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU Affero General Public License for more details.

You should have received a copy of the GNU Affero General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System;
using System.Xml;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace PoorMansTSqlFormatterLib.Parsers
{
    public class TSqlStandardParser : Interfaces.ISqlTokenParser
    {
        /*
         * TODO:
         *  - handle BETWEEN as a container, to easily avoid "AND" being treated as a boolean operator
         *  - handle CTEs such that AS clause is on its own line
         *  - enhance DDL context to also have clauses (with a backtrack in the standard formatter), for RETURNS...? Or just detect it in formatting?
         *  - update the demo UI to reference GPL, and publish the program
         *  - Add support for join hints, such as "LOOP"
         *  - Manually review the output from all test cases for "strange" effects
         *  - parse ON sections, for those who prefer to start ON on the next line and indent from there
         *  
         *  - Tests
         *    - Samples illustrating all the tokens and container combinations implemented
         *    - Samples illustrating all forms of container violations
         *    - Sample requests and their XML equivalent - once the xml format is more-or-less formalized
         *    - Sample requests and their formatted versions (a few for each) - once the "standard" format is more-or-less formalized
         */

        public XmlDocument ParseSQL(XmlDocument tokenListDoc)
        {
            XmlDocument sqlTree = new XmlDocument();
            XmlElement firstStatement;
            XmlElement currentContainerNode;
            bool errorFound = false;
            bool dataShufflingForced = false;

            if (tokenListDoc.SelectSingleNode(string.Format("/{0}/@{1}[.=1]", Interfaces.Constants.ENAME_SQLTOKENS_ROOT, Interfaces.Constants.ANAME_ERRORFOUND)) != null)
                errorFound = true;

            sqlTree.AppendChild(sqlTree.CreateElement(Interfaces.Constants.ENAME_SQL_ROOT));
            firstStatement = sqlTree.CreateElement(Interfaces.Constants.ENAME_SQL_STATEMENT);
            currentContainerNode = sqlTree.CreateElement(Interfaces.Constants.ENAME_SQL_CLAUSE);
            firstStatement.AppendChild(currentContainerNode);
            sqlTree.DocumentElement.AppendChild(firstStatement);

            XmlNodeList tokenList = tokenListDoc.SelectNodes(string.Format("/{0}/*", Interfaces.Constants.ENAME_SQLTOKENS_ROOT));
            int tokenCount = tokenList.Count;
            int tokenID = 0;
            while (tokenID < tokenCount)
            {
                XmlElement token = (XmlElement)tokenList[tokenID];

                switch (token.Name)
                {
                    case Interfaces.Constants.ENAME_PARENS_OPEN:

                        XmlElement firstNonCommentParensSibling = GetFirstNonWhitespaceNonCommentChildElement(currentContainerNode);
                        bool isInsertClause = (
                            firstNonCommentParensSibling != null
                            && firstNonCommentParensSibling.Name.Equals(Interfaces.Constants.ENAME_OTHERNODE)
                            && firstNonCommentParensSibling.InnerText.ToUpper().StartsWith("INSERT")
                            );

                        if (IsLatestTokenADDLDetailValue(currentContainerNode))
                            currentContainerNode = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_DDLDETAIL_PARENS, "", currentContainerNode);
                        else if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_DDL_BLOCK)
                            || isInsertClause
                            )
                            currentContainerNode = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_DDL_PARENS, "", currentContainerNode);
                        else if (IsLatestTokenAMiscName(currentContainerNode))
                            currentContainerNode = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_FUNCTION_PARENS, "", currentContainerNode);
                        else
                            currentContainerNode = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_EXPRESSION_PARENS, "", currentContainerNode);
                        break;

                    case Interfaces.Constants.ENAME_PARENS_CLOSE:
                        //check whether we expected to end the parens...
                        if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_DDLDETAIL_PARENS)
                            || currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_DDL_PARENS)
                            || currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_FUNCTION_PARENS)
                            || currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_EXPRESSION_PARENS)
                            )
                        {
                            currentContainerNode = (XmlElement)currentContainerNode.ParentNode;
                        }
                        else if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_SQL_CLAUSE)
                                && currentContainerNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_EXPRESSION_PARENS)
                                )
                        {
                            currentContainerNode = (XmlElement)currentContainerNode.ParentNode.ParentNode;
                        }
                        else
                        {
                            SaveNewElementWithError(sqlTree, Interfaces.Constants.ENAME_OTHERNODE, ")", currentContainerNode, ref errorFound);
                        }
                        break;

                    case Interfaces.Constants.ENAME_OTHERNODE:

                        //prepare multi-keyword detection by "peeking" up to 4 keywords ahead
                        List<List<XmlElement>> compoundKeywordOverflowNodes = null;
                        List<int> compoundKeywordTokenCounts = null;
                        List<string> compoundKeywordRawStrings = null;
                        string keywordMatchPhrase = GetKeywordMatchPhrase(tokenList, tokenID, ref compoundKeywordRawStrings, ref compoundKeywordTokenCounts, ref compoundKeywordOverflowNodes);
                        int keywordMatchStringsUsed = 0;

                        if (keywordMatchPhrase.StartsWith("CREATE ")
                            || keywordMatchPhrase.StartsWith("ALTER ")
                            || keywordMatchPhrase.StartsWith("DECLARE ")
                            )
                        {
                            ConsiderStartingNewStatement(sqlTree, ref currentContainerNode);
                            currentContainerNode = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_DDL_BLOCK, "", currentContainerNode);
                            SaveNewElement(sqlTree, token.Name, token.InnerText, currentContainerNode);
                        }
                        else if (keywordMatchPhrase.StartsWith("AS ") 
                            && currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_DDL_BLOCK)
                            )
                        {
                            XmlElement newASBlock = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_DDL_AS_BLOCK, token.InnerText, currentContainerNode);
                            currentContainerNode = StartNewStatement(sqlTree, newASBlock);
                        }
                        else if (keywordMatchPhrase.StartsWith("BEGIN TRANSACTION "))
                        {
                            ConsiderStartingNewStatement(sqlTree, ref currentContainerNode);
                            keywordMatchStringsUsed = 2;
                            ProcessCompoundKeyword(sqlTree, Interfaces.Constants.ENAME_BEGIN_TRANSACTION, ref tokenID, currentContainerNode, keywordMatchStringsUsed, compoundKeywordTokenCounts, compoundKeywordRawStrings);
                        }
                        else if (keywordMatchPhrase.StartsWith("COMMIT TRANSACTION "))
                        {
                            ConsiderStartingNewStatement(sqlTree, ref currentContainerNode);
                            keywordMatchStringsUsed = 2;
                            ProcessCompoundKeyword(sqlTree, Interfaces.Constants.ENAME_COMMIT_TRANSACTION, ref tokenID, currentContainerNode, keywordMatchStringsUsed, compoundKeywordTokenCounts, compoundKeywordRawStrings);
                        }
                        else if (keywordMatchPhrase.StartsWith("ROLLBACK TRANSACTION "))
                        {
                            ConsiderStartingNewStatement(sqlTree, ref currentContainerNode);
                            keywordMatchStringsUsed = 2;
                            ProcessCompoundKeyword(sqlTree, Interfaces.Constants.ENAME_ROLLBACK_TRANSACTION, ref tokenID, currentContainerNode, keywordMatchStringsUsed, compoundKeywordTokenCounts, compoundKeywordRawStrings);
                        }
                        else if (keywordMatchPhrase.StartsWith("BEGIN TRY "))
                        {
                            ConsiderStartingNewStatement(sqlTree, ref currentContainerNode);
                            keywordMatchStringsUsed = 2;
                            XmlElement newTryBlock = ProcessCompoundKeyword(sqlTree, Interfaces.Constants.ENAME_TRY_BLOCK, ref tokenID, currentContainerNode, keywordMatchStringsUsed, compoundKeywordTokenCounts, compoundKeywordRawStrings);
                            currentContainerNode = StartNewStatement(sqlTree, newTryBlock);
                        }
                        else if (keywordMatchPhrase.StartsWith("BEGIN CATCH "))
                        {
                            ConsiderStartingNewStatement(sqlTree, ref currentContainerNode);
                            keywordMatchStringsUsed = 2;
                            XmlElement newCatchBlock = ProcessCompoundKeyword(sqlTree, Interfaces.Constants.ENAME_CATCH_BLOCK, ref tokenID, currentContainerNode, keywordMatchStringsUsed, compoundKeywordTokenCounts, compoundKeywordRawStrings);
                            currentContainerNode = StartNewStatement(sqlTree, newCatchBlock);
                        }
                        else if (keywordMatchPhrase.StartsWith("BEGIN "))
                        {
                            ConsiderStartingNewStatement(sqlTree, ref currentContainerNode);
                            XmlElement newBeginBlock = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_BEGIN_END_BLOCK, token.InnerText, currentContainerNode);
                            currentContainerNode = StartNewStatement(sqlTree, newBeginBlock);
                        }
                        else if (keywordMatchPhrase.StartsWith("CASE "))
                        {
                            XmlElement newCaseStatement = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_CASE_STATEMENT, token.InnerText, currentContainerNode);
                            currentContainerNode = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_CASE_INPUT, "", newCaseStatement);
                        }
                        else if (keywordMatchPhrase.StartsWith("WHEN "))
                        {
                            if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_CASE_INPUT))
                                currentContainerNode = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_CASE_WHEN, token.InnerText, (XmlElement)currentContainerNode.ParentNode);
                            else if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_CASE_THEN))
                                currentContainerNode = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_CASE_WHEN, token.InnerText, (XmlElement)currentContainerNode.ParentNode.ParentNode);
                            else
                                SaveNewElementWithError(sqlTree, Interfaces.Constants.ENAME_OTHERNODE, token.InnerText, currentContainerNode, ref errorFound);
                        }
                        else if (keywordMatchPhrase.StartsWith("THEN "))
                        {
                            if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_CASE_WHEN))
                                currentContainerNode = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_CASE_THEN, token.InnerText, currentContainerNode);
                            else
                                SaveNewElementWithError(sqlTree, Interfaces.Constants.ENAME_OTHERNODE, token.InnerText, currentContainerNode, ref errorFound);
                        }
                        else if (keywordMatchPhrase.StartsWith("END TRY "))
                        {
                            EscapeAnySingleStatementContainers(ref currentContainerNode);

                            keywordMatchStringsUsed = 2;
                            string keywordString = GetCompoundKeyword(ref tokenID, keywordMatchStringsUsed, compoundKeywordTokenCounts, compoundKeywordRawStrings);

                            if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_SQL_CLAUSE)
                                && currentContainerNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT)
                                && currentContainerNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_TRY_BLOCK))
                            {
                                currentContainerNode.ParentNode.ParentNode.AppendChild(sqlTree.CreateTextNode(keywordString));
                                currentContainerNode = (XmlElement)currentContainerNode.ParentNode.ParentNode.ParentNode;
                            }
                            else
                            {
                                SaveNewElementWithError(sqlTree, Interfaces.Constants.ENAME_OTHERNODE, keywordString, currentContainerNode, ref errorFound);
                            }
                        }
                        else if (keywordMatchPhrase.StartsWith("END CATCH "))
                        {
                            EscapeAnySingleStatementContainers(ref currentContainerNode);

                            keywordMatchStringsUsed = 2;
                            string keywordString = GetCompoundKeyword(ref tokenID, keywordMatchStringsUsed, compoundKeywordTokenCounts, compoundKeywordRawStrings);

                            if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_SQL_CLAUSE)
                                && currentContainerNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT)
                                && currentContainerNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_CATCH_BLOCK))
                            {
                                currentContainerNode.ParentNode.ParentNode.AppendChild(sqlTree.CreateTextNode(keywordString));
                                currentContainerNode = (XmlElement)currentContainerNode.ParentNode.ParentNode.ParentNode;
                            }
                            else
                            {
                                SaveNewElementWithError(sqlTree, Interfaces.Constants.ENAME_OTHERNODE, keywordString, currentContainerNode, ref errorFound);
                            }
                        }
                        else if (keywordMatchPhrase.StartsWith("END "))
                        {
                            if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_CASE_THEN))
                            {
                                currentContainerNode.ParentNode.ParentNode.AppendChild(sqlTree.CreateTextNode(token.InnerText));
                                currentContainerNode = (XmlElement)currentContainerNode.ParentNode.ParentNode.ParentNode;
                            }
                            else if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_CASE_ELSE))
                            {
                                currentContainerNode.ParentNode.AppendChild(sqlTree.CreateTextNode(token.InnerText));
                                currentContainerNode = (XmlElement)currentContainerNode.ParentNode.ParentNode;
                            }
                            else
                            {
                                //Begin/End block handling
                                EscapeAnySingleStatementContainers(ref currentContainerNode);

                                if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_SQL_CLAUSE)
                                    && currentContainerNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT)
                                    && currentContainerNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_BEGIN_END_BLOCK))
                                {
                                    currentContainerNode.ParentNode.ParentNode.AppendChild(sqlTree.CreateTextNode(token.InnerText));
                                    currentContainerNode = (XmlElement)currentContainerNode.ParentNode.ParentNode.ParentNode;
                                }
                                else
                                {
                                    SaveNewElementWithError(sqlTree, Interfaces.Constants.ENAME_OTHERNODE, token.InnerText, currentContainerNode, ref errorFound);
                                }
                            }
                        }
                        else if (keywordMatchPhrase.StartsWith("GO "))
                        {
                            EscapeAnySingleStatementContainers(ref currentContainerNode);

                            //this looks a little simplistic... might need to review.
                            if ((token.PreviousSibling == null || IsLineBreakingWhiteSpace((XmlElement)token.PreviousSibling))
                                && (token.NextSibling == null || IsLineBreakingWhiteSpace((XmlElement)token.NextSibling))
                                )
                            {
                                // we found a batch separator - were we supposed to?
                                if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_SQL_CLAUSE)
                                    && currentContainerNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT)
                                    && (
                                        currentContainerNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_ROOT)
                                        || currentContainerNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_DDL_AS_BLOCK)
                                        )
                                    )
                                {
                                    XmlElement sqlRoot = sqlTree.DocumentElement;
                                    SaveNewElement(sqlTree, Interfaces.Constants.ENAME_BATCH_SEPARATOR, token.InnerText, sqlRoot);
                                    currentContainerNode = StartNewStatement(sqlTree, sqlRoot);
                                }
                                else
                                {
                                    SaveNewElementWithError(sqlTree, token.Name, token.InnerText, currentContainerNode, ref errorFound);
                                }
                            }
                            else
                            {
                                SaveNewElement(sqlTree, token.Name, token.InnerText, currentContainerNode);
                            }
                        }
                        else if (keywordMatchPhrase.StartsWith("JOIN "))
                        {
                            ConsiderStartingNewClause(sqlTree, ref currentContainerNode);
                            SaveNewElement(sqlTree, token.Name, token.InnerText, currentContainerNode);
                        }
                        else if (keywordMatchPhrase.StartsWith("LEFT JOIN ")
                            || keywordMatchPhrase.StartsWith("RIGHT JOIN ")
                            || keywordMatchPhrase.StartsWith("INNER JOIN ")
                            || keywordMatchPhrase.StartsWith("CROSS JOIN ")
                            || keywordMatchPhrase.StartsWith("CROSS APPLY ")
                            || keywordMatchPhrase.StartsWith("OUTER APPLY ")
                            )
                        {
                            ConsiderStartingNewClause(sqlTree, ref currentContainerNode);
                            keywordMatchStringsUsed = 2;
                            string keywordString = GetCompoundKeyword(ref tokenID, keywordMatchStringsUsed, compoundKeywordTokenCounts, compoundKeywordRawStrings);
                            SaveNewElement(sqlTree, token.Name, keywordString, currentContainerNode);
                        }
                        else if (keywordMatchPhrase.StartsWith("FULL OUTER JOIN ")
                            || keywordMatchPhrase.StartsWith("LEFT OUTER JOIN ")
                            || keywordMatchPhrase.StartsWith("RIGHT OUTER JOIN ")
                            )
                        {
                            ConsiderStartingNewClause(sqlTree, ref currentContainerNode);
                            keywordMatchStringsUsed = 3;
                            string keywordString = GetCompoundKeyword(ref tokenID, keywordMatchStringsUsed, compoundKeywordTokenCounts, compoundKeywordRawStrings);
                            SaveNewElement(sqlTree, token.Name, keywordString, currentContainerNode);
                        }
                        else if (keywordMatchPhrase.StartsWith("UNION ALL "))
                        {
                            ConsiderStartingNewClause(sqlTree, ref currentContainerNode);
                            keywordMatchStringsUsed = 2;
                            string keywordString = GetCompoundKeyword(ref tokenID, keywordMatchStringsUsed, compoundKeywordTokenCounts, compoundKeywordRawStrings);
                            SaveNewElement(sqlTree, Interfaces.Constants.ENAME_UNION_CLAUSE, keywordString, currentContainerNode);
                            currentContainerNode = (XmlElement)currentContainerNode.ParentNode;
                        }
                        else if (keywordMatchPhrase.StartsWith("UNION "))
                        {
                            ConsiderStartingNewClause(sqlTree, ref currentContainerNode);
                            SaveNewElement(sqlTree, Interfaces.Constants.ENAME_UNION_CLAUSE, token.InnerText, currentContainerNode);
                            currentContainerNode = (XmlElement)currentContainerNode.ParentNode;
                        }
                        else if (keywordMatchPhrase.StartsWith("WHILE "))
                        {
                            ConsiderStartingNewStatement(sqlTree, ref currentContainerNode);
                            XmlElement newWhileLoop = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_WHILE_LOOP, token.InnerText, currentContainerNode);
                            currentContainerNode = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_BOOLEAN_EXPRESSION, "", newWhileLoop);
                        }
                        else if (keywordMatchPhrase.StartsWith("IF "))
                        {
                            ConsiderStartingNewStatement(sqlTree, ref currentContainerNode);
                            XmlElement newIfStatement = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_IF_STATEMENT, token.InnerText, currentContainerNode);
                            currentContainerNode = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_BOOLEAN_EXPRESSION, "", newIfStatement);
                        }
                        else if (keywordMatchPhrase.StartsWith("ELSE "))
                        {
                            if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_CASE_THEN))
                            {
                                currentContainerNode = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_CASE_ELSE, token.InnerText, (XmlElement)currentContainerNode.ParentNode.ParentNode);
                            }
                            else if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_SQL_CLAUSE)
                                    && currentContainerNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT)
                                    && currentContainerNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_IF_STATEMENT))
                            {
                                //topmost if - just pop back one.
                                XmlElement containerIf = (XmlElement)currentContainerNode.ParentNode.ParentNode;
                                XmlElement newElseClause = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_ELSE_CLAUSE, token.InnerText, containerIf);
                                currentContainerNode = StartNewStatement(sqlTree, newElseClause);
                            }
                            else if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_SQL_CLAUSE)
                                    && currentContainerNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT)
                                    && currentContainerNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_ELSE_CLAUSE)
                                    && currentContainerNode.ParentNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_IF_STATEMENT)
                                )
                            {
                                //not topmost if; we need to pop up the single-statement containers stack to the next "if" that doesn't have an "else".
                                XmlElement currentNode = (XmlElement)currentContainerNode.ParentNode.ParentNode.ParentNode;
                                while (currentNode != null
                                    && (currentNode.Name.Equals(Interfaces.Constants.ENAME_WHILE_LOOP)
                                        || currentNode.SelectSingleNode(Interfaces.Constants.ENAME_ELSE_CLAUSE) != null
                                        )
                                    )
                                {
                                    if (currentNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT)
                                        && currentNode.ParentNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_IF_STATEMENT)
                                        )
                                        currentNode = (XmlElement)currentNode.ParentNode.ParentNode.ParentNode;
                                    else if (currentNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT)
                                        && currentNode.ParentNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_WHILE_LOOP)
                                        )
                                        currentNode = (XmlElement)currentNode.ParentNode.ParentNode.ParentNode;
                                    else if (currentNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT)
                                        && currentNode.ParentNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_ELSE_CLAUSE)
                                        && currentNode.ParentNode.ParentNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_IF_STATEMENT)
                                        )
                                        currentNode = (XmlElement)currentNode.ParentNode.ParentNode.ParentNode.ParentNode;
                                    else
                                        currentNode = null;
                                }

                                if (currentNode != null)
                                {
                                    XmlElement newElseClause2 = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_ELSE_CLAUSE, token.InnerText, currentNode);
                                    currentContainerNode = StartNewStatement(sqlTree, newElseClause2);
                                }
                                else
                                {
                                    SaveNewElementWithError(sqlTree, token.Name, token.InnerText, currentContainerNode, ref errorFound);
                                }
                            }
                            else
                            {
                                SaveNewElementWithError(sqlTree, token.Name, token.InnerText, currentContainerNode, ref errorFound);
                            }
                        }
                        else if (keywordMatchPhrase.StartsWith("INSERT INTO "))
                        {
                            ConsiderStartingNewStatement(sqlTree, ref currentContainerNode);
                            ConsiderStartingNewClause(sqlTree, ref currentContainerNode);
                            keywordMatchStringsUsed = 2;
                            string keywordString = GetCompoundKeyword(ref tokenID, keywordMatchStringsUsed, compoundKeywordTokenCounts, compoundKeywordRawStrings);
                            SaveNewElement(sqlTree, token.Name, keywordString, currentContainerNode);
                        }
                        else if (keywordMatchPhrase.StartsWith("INSERT "))
                        {
                            ConsiderStartingNewStatement(sqlTree, ref currentContainerNode);
                            ConsiderStartingNewClause(sqlTree, ref currentContainerNode);
                            SaveNewElement(sqlTree, token.Name, token.InnerText, currentContainerNode);
                        }
                        else if (keywordMatchPhrase.StartsWith("SELECT "))
                        {
                            XmlElement firstNonCommentSibling = GetFirstNonWhitespaceNonCommentChildElement(currentContainerNode);
                            if (!(
                                    firstNonCommentSibling != null
                                    && firstNonCommentSibling.Name.Equals(Interfaces.Constants.ENAME_OTHERNODE)
                                    && firstNonCommentSibling.InnerText.ToUpper().StartsWith("INSERT")
                                    )
                                )
                                ConsiderStartingNewStatement(sqlTree, ref currentContainerNode);

                            ConsiderStartingNewClause(sqlTree, ref currentContainerNode);
                            SaveNewElement(sqlTree, token.Name, token.InnerText, currentContainerNode);
                        }
                        else if (keywordMatchPhrase.StartsWith("AND "))
                        {
                            SaveNewElement(sqlTree, Interfaces.Constants.ENAME_AND_OPERATOR, token.InnerText, currentContainerNode);
                        }
                        else if (keywordMatchPhrase.StartsWith("OR "))
                        {
                            SaveNewElement(sqlTree, Interfaces.Constants.ENAME_OR_OPERATOR, token.InnerText, currentContainerNode);
                        }
                        else
                        {
                            //miscellaneous single-word tokens, which may or may not be statement starters and/or clause starters

                            //check for statements starting...
                            if (IsStatementStarter(token))
                            {
                                ConsiderStartingNewStatement(sqlTree, ref currentContainerNode);
                            }

                            //check for statements starting...
                            if (IsClauseStarter(token))
                            {
                                ConsiderStartingNewClause(sqlTree, ref currentContainerNode);
                            }

                            SaveNewElement(sqlTree, token.Name, token.InnerText, currentContainerNode);
                        }

                        //handle any Overflow Nodes
                        if (keywordMatchStringsUsed > 1)
                        {
                            for (int i = 0; i < keywordMatchStringsUsed - 1; i++)
                            {
                                foreach (XmlElement overflowEntry in compoundKeywordOverflowNodes[i])
                                {
                                    currentContainerNode.AppendChild(overflowEntry);
                                    dataShufflingForced = true;
                                }
                            }
                        }

                        break;

                    case Interfaces.Constants.ENAME_SEMICOLON:
                        SaveNewElement(sqlTree, token.Name, token.InnerText, currentContainerNode);
                        ConsiderStartingNewStatement(sqlTree, ref currentContainerNode);
                        break;

                    case Interfaces.Constants.ENAME_COMMENT_MULTILINE:
                    case Interfaces.Constants.ENAME_COMMENT_SINGLELINE:
                    case Interfaces.Constants.ENAME_WHITESPACE:
                        //create in statement rather than clause if there are no siblings yet
                        if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_SQL_CLAUSE)
                            && currentContainerNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT)
                            && currentContainerNode.SelectSingleNode("*") == null
                            )
                            SaveNewElementAsPriorSibling(sqlTree, token.Name, token.InnerText, currentContainerNode);
                        else
                            SaveNewElement(sqlTree, token.Name, token.InnerText, currentContainerNode);
                        break;

                    case Interfaces.Constants.ENAME_QUOTED_IDENTIFIER:
                    case Interfaces.Constants.ENAME_ASTERISK:
                    case Interfaces.Constants.ENAME_COMMA:
                    case Interfaces.Constants.ENAME_PERIOD:
                    case Interfaces.Constants.ENAME_NSTRING:
                    case Interfaces.Constants.ENAME_OTHEROPERATOR:
                    case Interfaces.Constants.ENAME_STRING:
                        SaveNewElement(sqlTree, token.Name, token.InnerText, currentContainerNode);
                        break;
                    default:
                        throw new Exception("Unrecognized element encountered!");
                }

                tokenID++;
            }

            EscapeAnySingleStatementContainers(ref currentContainerNode);
            if (!currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_SQL_CLAUSE)
                || !currentContainerNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT)
                || (
                    !currentContainerNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_ROOT)
                    &&
                    !currentContainerNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_DDL_AS_BLOCK)
                    )
                )
            {
                errorFound = true;
#if DEBUG
                System.Diagnostics.Debugger.Break();
#endif
            }

            if (errorFound)
            {
                sqlTree.DocumentElement.SetAttribute(Interfaces.Constants.ANAME_ERRORFOUND, "1");
            }

            if (dataShufflingForced)
            {
                sqlTree.DocumentElement.SetAttribute(Interfaces.Constants.ANAME_DATALOSS, "1");
            }

            return sqlTree;
        }

        private string GetKeywordMatchPhrase(XmlNodeList tokenList, int tokenID, ref List<string> rawKeywordParts, ref List<int> tokenCounts, ref List<List<XmlElement>> overflowNodes)
        {
            string phrase = "";
            int phraseComponentsFound = 0;
            rawKeywordParts = new List<string>();
            overflowNodes = new List<List<XmlElement>>();
            tokenCounts = new List<int>();
            string precedingWhitespace = "";
            int originalTokenID = tokenID;

            while (tokenID < tokenList.Count && phraseComponentsFound < 4)
            {
                if (tokenList[tokenID].Name.Equals(Interfaces.Constants.ENAME_OTHERNODE)
                    || tokenList[tokenID].Name.Equals(Interfaces.Constants.ENAME_QUOTED_IDENTIFIER))
                {
                    phrase += tokenList[tokenID].InnerText.ToUpper() + " ";
                    phraseComponentsFound++;
                    rawKeywordParts.Add(precedingWhitespace + tokenList[tokenID].InnerText);

                    tokenID++;
                    tokenCounts.Add(tokenID - originalTokenID);

                    //found a possible phrase component - skip past any upcoming whitespace or comments, keeping track.
                    overflowNodes.Add(new List<XmlElement>());
                    precedingWhitespace = "";
                    while (tokenID < tokenList.Count
                        && (tokenList[tokenID].Name.Equals(Interfaces.Constants.ENAME_WHITESPACE)
                            || tokenList[tokenID].Name.Equals(Interfaces.Constants.ENAME_COMMENT_SINGLELINE)
                            || tokenList[tokenID].Name.Equals(Interfaces.Constants.ENAME_COMMENT_MULTILINE)
                            )
                        )
                    {
                        if (tokenList[tokenID].Name.Equals(Interfaces.Constants.ENAME_WHITESPACE))
                        {
                            precedingWhitespace += tokenList[tokenID].InnerText;
                        }
                        else
                        {
                            overflowNodes[phraseComponentsFound-1].Add((XmlElement)tokenList[tokenID]);
                        }
                        tokenID++;
                    }
                }
                else
                    //we're not interested in any other node types
                    break;
            }

            return phrase;
        }

        private XmlElement SaveNewElement(XmlDocument sqlTree, string newElementName, string newElementValue, XmlElement currentContainerNode)
        {
            XmlElement newElement = sqlTree.CreateElement(newElementName);
            newElement.InnerText = newElementValue;
            currentContainerNode.AppendChild(newElement);
            return newElement;
        }

        private XmlElement SaveNewElementAsPriorSibling(XmlDocument sqlTree, string newElementName, string newElementValue, XmlElement nodeToSaveBefore)
        {
            XmlElement newElement = sqlTree.CreateElement(newElementName);
            newElement.InnerText = newElementValue;
            nodeToSaveBefore.ParentNode.InsertBefore(newElement, nodeToSaveBefore);
            return newElement;
        }

        private void SaveNewElementWithError(XmlDocument sqlTree, string newElementName, string newElementValue, XmlElement currentContainerNode, ref bool errorFound)
        {
            SaveNewElement(sqlTree, newElementName, newElementValue, currentContainerNode);
#if DEBUG
            System.Diagnostics.Debugger.Break();
#endif
            errorFound = true;
        }

        private XmlElement ProcessCompoundKeyword(XmlDocument sqlTree, string newElementName, ref int tokenID, XmlElement currentContainerNode, int compoundKeywordCount, List<int> compoundKeywordTokenCounts, List<string> compoundKeywordRawStrings)
        {
            XmlElement newElement = sqlTree.CreateElement(newElementName);
            newElement.InnerText = GetCompoundKeyword(ref tokenID, compoundKeywordCount, compoundKeywordTokenCounts, compoundKeywordRawStrings);
            currentContainerNode.AppendChild(newElement);
            return newElement;
        }

        private string GetCompoundKeyword(ref int tokenID, int compoundKeywordCount, List<int> compoundKeywordTokenCounts, List<string> compoundKeywordRawStrings)
        {
            tokenID += compoundKeywordTokenCounts[compoundKeywordCount - 1] - 1;
            string outString = "";
            for (int i = 0; i < compoundKeywordCount; i++)
                outString += compoundKeywordRawStrings[i];
            return outString;
        }

        private void ConsiderStartingNewStatement(XmlDocument sqlTree, ref XmlElement currentContainerNode)
        {
            XmlElement previousContainerElement = currentContainerNode;

            if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_BOOLEAN_EXPRESSION)
                && (currentContainerNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_IF_STATEMENT)
                    || currentContainerNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_WHILE_LOOP)
                    )
                )
            {
                //we just ended the boolean clause of an if or while, and need to pop to the first (and only) statement.
                currentContainerNode = StartNewStatement(sqlTree, (XmlElement)currentContainerNode.ParentNode);
                MigrateApplicableComments(previousContainerElement, currentContainerNode);
            }
            else if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_SQL_CLAUSE)
                && currentContainerNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT)
                && HasNonWhiteSpaceNonSingleCommentContent(currentContainerNode)
                )
            {
                EscapeAnySingleStatementContainers(ref currentContainerNode);
                XmlElement inBetweenContainerElement = currentContainerNode;
                currentContainerNode = StartNewStatement(sqlTree, (XmlElement)currentContainerNode.ParentNode.ParentNode);
                if (!inBetweenContainerElement.Equals(previousContainerElement))
                    MigrateApplicableComments(inBetweenContainerElement, currentContainerNode);
                MigrateApplicableComments(previousContainerElement, currentContainerNode);
            }
            else if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_DDL_BLOCK))
            {
                EscapeAnySingleStatementContainers(ref currentContainerNode);
                XmlElement inBetweenContainerElement = currentContainerNode;
                currentContainerNode = StartNewStatement(sqlTree, (XmlElement)currentContainerNode.ParentNode.ParentNode.ParentNode);
                if (!inBetweenContainerElement.Equals(previousContainerElement))
                    MigrateApplicableComments(inBetweenContainerElement, currentContainerNode);
                MigrateApplicableComments(previousContainerElement, currentContainerNode);
            }
        }

        private XmlElement StartNewStatement(XmlDocument sqlTree, XmlElement containerElement)
        {
            XmlElement newStatement = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_SQL_STATEMENT, "", containerElement);
            return SaveNewElement(sqlTree, Interfaces.Constants.ENAME_SQL_CLAUSE, "", newStatement);
        }

        private void ConsiderStartingNewClause(XmlDocument sqlTree, ref XmlElement currentContainerNode)
        {
            if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_SQL_CLAUSE)
                && HasNonWhiteSpaceNonSingleCommentContent(currentContainerNode)
                )
            {
                //complete current clause, start a new one in the same container
                XmlElement previousContainerElement = currentContainerNode;
                currentContainerNode = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_SQL_CLAUSE, "", (XmlElement)currentContainerNode.ParentNode);
                MigrateApplicableComments(previousContainerElement, currentContainerNode);
            }
            else if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_EXPRESSION_PARENS)
                || currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT))
            {
                //create new clause and set context to it.
                currentContainerNode = SaveNewElement(sqlTree, Interfaces.Constants.ENAME_SQL_CLAUSE, "", currentContainerNode);
            }
        }

        private static void MigrateApplicableComments(XmlElement previousContainerElement, XmlElement currentContainerNode)
        {
            XmlNode migrationCandidate = previousContainerElement.LastChild;

            while (migrationCandidate != null)
            {
                if (migrationCandidate.NodeType == XmlNodeType.Whitespace
                    || migrationCandidate.Name.Equals(Interfaces.Constants.ENAME_WHITESPACE))
                {
                    migrationCandidate = migrationCandidate.PreviousSibling;
                    continue;
                }
                else if (migrationCandidate.PreviousSibling != null
                    && (migrationCandidate.Name.Equals(Interfaces.Constants.ENAME_COMMENT_SINGLELINE)
                        || migrationCandidate.Name.Equals(Interfaces.Constants.ENAME_COMMENT_MULTILINE)
                        )
                    && (migrationCandidate.PreviousSibling.NodeType == XmlNodeType.Whitespace
                        || migrationCandidate.PreviousSibling.Name.Equals(Interfaces.Constants.ENAME_WHITESPACE)
                        || migrationCandidate.PreviousSibling.Name.Equals(Interfaces.Constants.ENAME_COMMENT_SINGLELINE)
                        || migrationCandidate.PreviousSibling.Name.Equals(Interfaces.Constants.ENAME_COMMENT_MULTILINE)
                        )
                    )
                {
                    if ((migrationCandidate.PreviousSibling.NodeType == XmlNodeType.Whitespace
                            || migrationCandidate.PreviousSibling.Name.Equals(Interfaces.Constants.ENAME_WHITESPACE)
                            )
                        && Regex.IsMatch(migrationCandidate.PreviousSibling.InnerText, @"(\r|\n)+")
                        )
                    {
                        //migrate everything considered so far, and move on to the next one for consideration.
                        while (!previousContainerElement.LastChild.Equals(migrationCandidate))
                        {
                            currentContainerNode.ParentNode.PrependChild(previousContainerElement.LastChild);
                        }
                        currentContainerNode.ParentNode.PrependChild(migrationCandidate);
                        migrationCandidate = previousContainerElement.LastChild;
                    }
                    else
                    {
                        //this one wasn't properly separated from the previous node/entry, keep going in case there's a linebreak further up.
                        migrationCandidate = migrationCandidate.PreviousSibling;
                    }
                }
                else
                {
                    //we found a non-whitespace non-comment node. Stop trying to migrate comments.
                    migrationCandidate = null;
                }
            }
        }

        private static void EscapeAnySingleStatementContainers(ref XmlElement currentContainerNode)
        {
            while (true)
            {
                if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_SQL_CLAUSE)
                    && currentContainerNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT)
                    && (currentContainerNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_IF_STATEMENT)
                        || currentContainerNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_WHILE_LOOP)
                        )
                    )
                {

                    //we just ended the one statement of an if or while, and need to pop out to a new statement at the same level as the IF or WHILE
                    currentContainerNode = (XmlElement)currentContainerNode.ParentNode.ParentNode.ParentNode;
                }
                else if (currentContainerNode.Name.Equals(Interfaces.Constants.ENAME_SQL_CLAUSE)
                    && currentContainerNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_SQL_STATEMENT)
                    && currentContainerNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_ELSE_CLAUSE)
                    && currentContainerNode.ParentNode.ParentNode.ParentNode.Name.Equals(Interfaces.Constants.ENAME_IF_STATEMENT)
                    )
                {
                    //we just ended the one and only statement in an else clause, and need to pop out to a new statement at the same level as the IF
                    currentContainerNode = (XmlElement)currentContainerNode.ParentNode.ParentNode.ParentNode.ParentNode;
                }
                else
                {
                    break;
                }
            }
        }

        private XmlElement GetFirstNonWhitespaceNonCommentChildElement(XmlElement currentContainerNode)
        {
            XmlNode currentNode = currentContainerNode.FirstChild;
            while (currentNode != null)
            {
                if (IsCommentOrWhiteSpace(currentNode) || currentNode.NodeType != XmlNodeType.Element)
                    currentNode = currentNode.NextSibling;
                else
                    return (XmlElement)currentNode;
            }
            return null;
        }

        private static bool IsStatementStarter(XmlElement token)
        {
            string uppercaseValue = token.InnerText.ToUpper();
            return (token.Name.Equals(Interfaces.Constants.ENAME_OTHERNODE)
                && (uppercaseValue.Equals("SELECT")
                    || uppercaseValue.Equals("DELETE")
                    || uppercaseValue.Equals("INSERT")
                    || uppercaseValue.Equals("UPDATE")
                    || uppercaseValue.Equals("IF")
                    || uppercaseValue.Equals("SET")
                    || uppercaseValue.Equals("CREATE")
                    || uppercaseValue.Equals("DROP")
                    || uppercaseValue.Equals("ALTER")
                    || uppercaseValue.Equals("TRUNCATE")
                    || uppercaseValue.Equals("DECLARE")
                    || uppercaseValue.Equals("EXEC")
                    || uppercaseValue.Equals("EXECUTE")
                    || uppercaseValue.Equals("WHILE")
                    || uppercaseValue.Equals("BREAK")
                    || uppercaseValue.Equals("CONTINUE")
                    || uppercaseValue.Equals("PRINT")
                    || uppercaseValue.Equals("USE")
                    || uppercaseValue.Equals("RETURN")
                    || uppercaseValue.Equals("WAITFOR")
                    || uppercaseValue.Equals("RAISERROR")
                    || uppercaseValue.Equals("COMMIT")
                    || uppercaseValue.Equals("OPEN")
                    || uppercaseValue.Equals("FETCH")
                    || uppercaseValue.Equals("CLOSE")
                    || uppercaseValue.Equals("DEALLOCATE")
                    )
                );
        }

        private static bool IsClauseStarter(XmlElement token)
        {
            string uppercaseValue = token.InnerText.ToUpper();
            return (token.Name.Equals(Interfaces.Constants.ENAME_OTHERNODE)
                && (uppercaseValue.Equals("INNER")
                    || uppercaseValue.Equals("LEFT")
                    || uppercaseValue.Equals("JOIN")
                    || uppercaseValue.Equals("WHERE")
                    || uppercaseValue.Equals("FROM")
                    || uppercaseValue.Equals("ORDER")
                    || uppercaseValue.Equals("GROUP")
                    || uppercaseValue.Equals("HAVING")
                    || uppercaseValue.Equals("INTO")
                    || uppercaseValue.Equals("SELECT")
                    || uppercaseValue.Equals("UNION")
                    || uppercaseValue.Equals("VALUES")
                    || uppercaseValue.Equals("RETURNS")
                    || uppercaseValue.Equals("FOR")
                    || uppercaseValue.Equals("PIVOT")
                    || uppercaseValue.Equals("UNPIVOT")
                    )
                );
        }

        private bool IsLatestTokenADDLDetailValue(XmlElement currentContainerNode)
        {
            XmlNode currentNode = currentContainerNode.LastChild;
            while (currentNode != null)
            {
                if (currentNode.Name.Equals(Interfaces.Constants.ENAME_OTHERNODE)
                    && (
                        currentNode.InnerText.ToUpper().Equals("NVARCHAR")
                        || currentNode.InnerText.ToUpper().Equals("VARCHAR")
                        || currentNode.InnerText.ToUpper().Equals("DECIMAL")
                        || currentNode.InnerText.ToUpper().Equals("NUMERIC")
                        || currentNode.InnerText.ToUpper().Equals("VARBINARY")
                        || currentNode.InnerText.ToUpper().Equals("DEFAULT") //TODO: not really a data type, I'll have to rename the objects 
                        || currentNode.InnerText.ToUpper().Equals("IDENTITY") //TODO: not really a data type, I'll have to rename the objects 
                        )
                    )
                {
                    return true;
                }
                else if (IsCommentOrWhiteSpace(currentNode))
                {
                    currentNode = currentNode.PreviousSibling;
                }
                else 
                    currentNode = null;
            }
            return false;
        }

        private bool IsLatestTokenAMiscName(XmlElement currentContainerNode)
        {
            XmlNode currentNode = currentContainerNode.LastChild;
            while (currentNode != null)
            {
                string testValue = currentNode.InnerText.ToUpper();
                if (currentNode.Name.Equals(Interfaces.Constants.ENAME_QUOTED_IDENTIFIER)
                    || (currentNode.Name.Equals(Interfaces.Constants.ENAME_OTHERNODE)
                        && !(testValue.Equals("AND")
                            || testValue.Equals("OR")
                            || testValue.Equals("NOT")
                            || testValue.Equals("BETWEEN")
                            || testValue.Equals("LIKE")
                            || testValue.Equals("CONTAINS")
                            || testValue.Equals("EXISTS")
                            || testValue.Equals("FREETEXT")
                            || testValue.Equals("IN")
                            || testValue.Equals("ALL")
                            || testValue.Equals("SOME")
                            || testValue.Equals("ANY")
                            || testValue.Equals("FROM")
                            || testValue.Equals("JOIN")
                            || testValue.EndsWith(" JOIN")
                            || testValue.Equals("UNION")
                            || testValue.Equals("UNION ALL")
                            || testValue.Equals("AS")
                            || testValue.EndsWith(" APPLY")
                            )
                        )
                    )
                {
                    return true;
                }
                else if (IsCommentOrWhiteSpace(currentNode))
                {
                    currentNode = currentNode.PreviousSibling;
                }
                else
                    currentNode = null;
            }
            return false;
        }

        private static bool IsLineBreakingWhiteSpace(XmlElement token)
        {
            return (token.Name.Equals(Interfaces.Constants.ENAME_WHITESPACE) 
                && Regex.IsMatch(token.InnerText, @"(\r|\n)+"));
        }

        private bool IsCommentOrWhiteSpace(XmlNode currentNode)
        {
            return (currentNode.Name.Equals(Interfaces.Constants.ENAME_WHITESPACE)
                || currentNode.Name.Equals(Interfaces.Constants.ENAME_COMMENT_SINGLELINE)
                || currentNode.Name.Equals(Interfaces.Constants.ENAME_COMMENT_MULTILINE)
                );
        }

        private static bool HasNonWhiteSpaceNonSingleCommentContent(XmlElement containerNode)
        {
            foreach (XmlElement testElement in containerNode.SelectNodes("*"))
                if (!testElement.Name.Equals(Interfaces.Constants.ENAME_WHITESPACE)
                    && !testElement.Name.Equals(Interfaces.Constants.ENAME_COMMENT_SINGLELINE)
                    && (!testElement.Name.Equals(Interfaces.Constants.ENAME_COMMENT_MULTILINE)
                        || Regex.IsMatch(testElement.InnerText, @"(\r|\n)+")
                        )
                    )
                    return true;

            return false;
        }
    }
}